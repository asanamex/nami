using System.Runtime.InteropServices;
using Nami;
using Nami.Wave.Internal;

namespace Nami.Wave;

/// <summary>
/// IL2CPP method patching - Wave's game-side engine (the IL-copy engine, Wave.Patch,
/// works on CoreCLR methods only; IL2CPP methods are native x64 code in GameAssembly.dll).
///
/// Two hook shapes:
///
/// <see cref="Hook"/> - prefix observer + skip semantics on the raw x64 argument
/// registers (rcx/rdx/r8/r9): for instance methods args[0] is <c>this</c> (an
/// Il2CppObject*), for statics args[0] is the first parameter. Stack arguments (5+)
/// are not exposed and the skip return value is 0 (fast path).
///
/// <see cref="HookFull"/> - observes and rewrites RESULTS and exposes ALL arguments
/// (register + stack, up to 12): prefix (may skip, optionally supplying a replacement
/// result) then the original, then postfix (may rewrite the result the caller sees).
/// Value-typed and floating-point results are marshaled through a 2-slot result
/// pointer per <see cref="Il2CppReturnKind"/> (slot [0] = rax bits, slot [1] = xmm0
/// bits).
///
/// Both dispatch on the game's main thread (window-proc executor), so callbacks must
/// not block. Install happens on the game main thread (il2cpp resolution is only safe
/// there); the game window must exist - on IL2CPP titles call
/// <c>Tide.EnsureReady()</c> first.
/// </summary>
public static unsafe class WaveIl2Cpp
{
    /// <summary>How the native result register is interpreted (see HookFull).</summary>
    public enum Il2CppReturnKind
    {
        Void = 0,
        I32 = 1,
        I64 = 2,
        F32 = 3,
        F64 = 4,
    }

    /// <summary>
    /// Typed, callback-scoped access to an IL2CPP hook invocation. Values use the same
    /// TideValue vocabulary as GameClass/GameObject.
    /// Object values are borrowed handles valid only during the callback: wrap with
    /// GetObject/GetResultObject (or GetArgument&lt;GameObject&gt;) for reads, do NOT
    /// Dispose them (the native frame frees its temporaries after dispatch; disposing
    /// would double-free the IL2CPP GC handle). Instance receivers are available through
    /// the borrowed <see cref="This"/> property.
    /// </summary>
    public sealed class Il2CppHookContext : IDisposable
    {
        private nint _nativeFrame;
        private bool _cleaned;

        internal Il2CppHookContext(nint nativeFrame) => _nativeFrame = nativeFrame;

        public int ArgumentCount
        {
            get { EnsureLive(); return NativeFrameArgCount(_nativeFrame); }
        }

        public bool IsInstanceMethod
        {
            get { EnsureLive(); return NativeFrameIsInstance(_nativeFrame) != 0; }
        }
        /// <summary>
        /// The borrowed receiver for instance-method hooks (null on static methods).
        /// Valid only during the callback; do NOT Dispose (the native frame frees its
        /// temporaries via nami_il2cpp_hook_frame_cleanup after dispatch - Dispose is a
        /// no-op guard, but the handle still dangles after the callback returns).
        /// </summary>
        public GameObject? This
        {
            get
            {
                EnsureLive();
                if (!IsInstanceMethod)
                {
                    return null;
                }

                var value = default(TideValue);
                var rc = NativeFrameGetThis(_nativeFrame, &value);
                if (rc != 0)
                {
                    throw new Il2CppHookException($"cannot read typed IL2CPP receiver (code {rc})");
                }

                return value.Type == TideType.Object ? GameObject.FromBorrowedHandle(value.Handle) : null;
            }
        }

        public TideType ResultType
        {
            get { EnsureLive(); return (TideType)NativeFrameGetResultType(_nativeFrame); }
        }

        public TideType GetArgumentType(int index)
        {
            EnsureLive();
            return (TideType)NativeFrameGetType(_nativeFrame, index);
        }

        public TideValue GetArgument(int index)
        {
            EnsureLive();
            var value = default(TideValue);
            var rc = NativeFrameGet(_nativeFrame, index, &value);
            if (rc != 0) throw new Il2CppHookException($"cannot read typed IL2CPP argument {index} (code {rc})");
            return value;
        }

        public void SetArgument(int index, TideValue value)
        {
            EnsureLive();
            var rc = NativeFrameSet(_nativeFrame, index, &value);
            if (rc != 0) throw new Il2CppHookException($"cannot write typed IL2CPP argument {index} (code {rc})");
        }

        public void SetArgument(int index, string? value)
        {
            var typed = TideValue.FromString(value);
            try
            {
                SetArgument(index, typed);
            }
            finally
            {
                typed.FreeStringBuffer();
            }
        }

        public TideValue GetResult()
        {
            EnsureLive();
            var value = default(TideValue);
            var rc = NativeFrameGetResult(_nativeFrame, &value);
            if (rc != 0) throw new Il2CppHookException($"cannot read typed IL2CPP result (code {rc})");
            return value;
        }

        public void SetResult(TideValue value)
        {
            EnsureLive();
            var rc = NativeFrameSetResult(_nativeFrame, &value);
            if (rc != 0) throw new Il2CppHookException($"cannot write typed IL2CPP result (code {rc})");
        }

        public void SetResult(string? value)
        {
            var typed = TideValue.FromString(value);
            try
            {
                SetResult(typed);
            }
            finally
            {
                typed.FreeStringBuffer();
            }
        }

        /// <summary>Reads and frees a string argument returned by the native decoder.</summary>
        public string? GetString(int index)
        {
            var value = GetArgument(index);
            try
            {
                return value.String;
            }
            finally
            {
                value.FreeNativeReturn();
            }
        }

        /// <summary>Borrowed read: valid during the callback only, do NOT Dispose.</summary>
        public GameObject? GetObject(int index)
        {
            var value = GetArgument(index);
            return value.Type == TideType.Object ? GameObject.FromBorrowedHandle(value.Handle) : null;
        }

        /// <summary>Strongly-typed argument read. Enums map via their underlying int/long.</summary>
        public T? GetArgument<T>(int index)
        {
            var value = GetArgument(index);
            return ConvertHookValue<T>(value);
        }

        /// <summary>Strongly-typed argument write. Null maps to the spec's null shape.</summary>
        public void SetArgument<T>(int index, T? value)
        {
            var typed = ToHookValue(index, value);
            try
            {
                SetArgument(index, typed);
            }
            finally
            {
                typed.FreeStringBuffer();
            }
        }

        /// <summary>Strongly-typed result read. Enums map via their underlying int/long.</summary>
        public T? GetResult<T>()
        {
            var value = GetResult();
            return ConvertHookValue<T>(value);
        }

        /// <summary>Strongly-typed result write.</summary>
        public void SetResult<T>(T? value)
        {
            var typed = ToHookValue(null, value);
            try
            {
                SetResult(typed);
            }
            finally
            {
                typed.FreeStringBuffer();
            }
        }

        private TideValue ToHookValue<T>(int? index, T? value)
        {
            if (value is null)
            {
                var spec = index.HasValue ? GetArgumentType(index.Value) : ResultType;
                return spec == TideType.String ? TideValue.FromString(null) : TideValue.FromHandle(0);
            }

            var t = typeof(T);
            if (t.IsEnum)
            {
                return Type.GetTypeCode(Enum.GetUnderlyingType(t)) == TypeCode.Int64
                    ? TideValue.FromLong(System.Convert.ToInt64(value))
                    : TideValue.FromInt(System.Convert.ToInt32(value));
            }

            return value switch
            {
                int i => TideValue.FromInt(i),
                long l => TideValue.FromLong(l),
                float f => TideValue.FromFloat(f),
                double d => TideValue.FromDouble(d),
                bool b => TideValue.FromBool(b),
                string s => TideValue.FromString(s),
                GameObject go => TideValue.FromHandle(go.HandleValue),
                _ => throw new NotSupportedException($"type {t} is not supported by typed IL2CPP hooks"),
            };
        }

        internal static T? ConvertHookValue<T>(TideValue v)
        {
            var t = typeof(T);
            if (t.IsEnum)
            {
                return (T)Enum.ToObject(t, v.Type == TideType.I64 ? v.Int64 : v.Int32);
            }

            if (t == typeof(int)) return (T)(object)v.Int32;
            if (t == typeof(long)) return (T)(object)v.Int64;
            if (t == typeof(float)) return (T)(object)v.Single;
            if (t == typeof(double)) return (T)(object)v.Double;
            if (t == typeof(bool)) return (T)(object)v.Boolean;
            if (t == typeof(string))
            {
                try
                {
                    var s = v.String;
                    return s is null ? default : (T)(object)s;
                }
                finally
                {
                    v.FreeNativeReturn();
                }
            }

            if (typeof(GameObject).IsAssignableFrom(t) && v.Type == TideType.Object)
            {
                return (T)(object)GameObject.FromBorrowedHandle(v.Handle);
            }

            return default;
        }

        /// <summary>Reads and frees a string result returned by the native decoder.</summary>
        public string? GetResultString()
        {
            var value = GetResult();
            try
            {
                return value.String;
            }
            finally
            {
                value.FreeNativeReturn();
            }
        }

        /// <summary>Borrowed read: valid during the callback only, do NOT Dispose.</summary>
        public GameObject? GetResultObject()
        {
            var value = GetResult();
            return value.Type == TideType.Object ? GameObject.FromBorrowedHandle(value.Handle) : null;
        }

        internal void Cleanup()
        {
            if (_cleaned) return;
            var frame = _nativeFrame;
            Invalidate();
            NativeFrameCleanup(frame);
        }

        internal void Invalidate()
        {
            _cleaned = true;
            _nativeFrame = IntPtr.Zero;
        }

        private void EnsureLive()
        {
            if (_cleaned || _nativeFrame == IntPtr.Zero) throw new ObjectDisposedException(nameof(Il2CppHookContext));
        }

        public void Dispose() => Cleanup();
    }

    public delegate bool Il2CppTypedHookPrefixCallback(Il2CppHookContext context);
    public delegate void Il2CppTypedHookPostfixCallback(Il2CppHookContext context);

    /// <summary>
    /// The fast-path patch callback. <paramref name="instance"/> is args[0] (an
    /// Il2CppObject* for instance methods, else the first parameter).
    /// <paramref name="args"/> points at the 4 raw argument-register slots
    /// (rcx/rdx/r8/r9); only <paramref name="argCount"/> are meaningful.
    /// Return true to SKIP the original method.
    /// </summary>
    public delegate bool Il2CppHookCallback(nint instance, nint* args, int argCount);

    /// <summary>
    /// Full-path prefix callback (see <see cref="HookFull"/>). Receives the instance
    /// (args[0]), every argument as contiguous raw slots (<paramref name="argCount"/>
    /// ≤ 12: registers first, then the caller's stack args), and the result slot
    /// (2 entries: [0] = rax bits, [1] = xmm0 bits). Return true to SKIP the original -
    /// to skip with a replacement value, write the slot first (rax slot for
    /// <see cref="Il2CppReturnKind.I32"/>/<see cref="Il2CppReturnKind.I64"/>, xmm0
    /// slot for F32/F64).
    /// </summary>
    public delegate bool Il2CppHookPrefixCallback(nint instance, nint* args, int argCount,
        nint* result, Il2CppReturnKind returnKind);

    /// <summary>
    /// Full-path postfix callback (see <see cref="HookFull"/>). Runs after the original
    /// with the same arguments and the observed result in <paramref name="result"/>;
    /// rewrite the slot (per <paramref name="returnKind"/>) to change what the caller
    /// receives.
    /// </summary>
    public delegate void Il2CppHookPostfixCallback(nint instance, nint* args, int argCount,
        nint* result, Il2CppReturnKind returnKind);

    /// <summary>Thrown when an IL2CPP hook cannot be installed.</summary>
    public sealed class Il2CppHookException : Exception
    {
        public Il2CppHookException(string message) : base(message) { }
        public Il2CppHookException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>An installed hook; Dispose unhooks it. Also removable by owner.</summary>
    public sealed class Il2CppHook : IDisposable
    {
        private readonly string _owner;
        private long _hookId;
        private readonly string _target;

        internal Il2CppHook(string owner, string target, long hookId)
        {
            _owner = owner;
            _target = target;
            _hookId = hookId;
        }

        internal long HookId => _hookId;

        /// <summary>Removes the hook (restores the original bytes). Idempotent.</summary>
        public void Dispose()
        {
            if (_hookId == 0)
            {
                return;
            }

            lock (RegistryLock)
            {
                if (!Sites.TryGetValue(_hookId, out var site))
                {
                    _hookId = 0;
                    return;
                }

                Sites.Remove(_hookId);
                _hookId = 0;
                NativeUnhook(site.HookId);
                site.Free();
            }
        }

        public override string ToString() => $"Il2CppHook({_target}, owner={_owner})";
    }

    // ------------------------------------------------------------ native surface

    private const string LoaderDll = "nami_loader";

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_available", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeIl2CppAvailable();

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_hook", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeHook(byte* assembly, byte* ns, byte* klass, byte* method,
        int argc, nint dispatch, nint dispatchPostfix, int returnKind, nint userHandle,
        out nint trampoline, out long hookId);

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_hook_typed", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeHookTyped(byte* assembly, byte* ns, byte* klass, byte* method,
        int argc, int* expectedTypes, int expectedCount, int expectedReturn,
        nint dispatchPrefix, nint dispatchPostfix, nint userHandle, out long hookId);

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_hook_frame_arg_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeFrameArgCount(nint frame);

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_hook_frame_get_type", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeFrameGetType(nint frame, int index);

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_hook_frame_is_instance", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeFrameIsInstance(nint frame);

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_hook_frame_get", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeFrameGet(nint frame, int index, TideValue* value);

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_hook_frame_set", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeFrameSet(nint frame, int index, TideValue* value);

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_hook_frame_get_this", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeFrameGetThis(nint frame, TideValue* value);

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_hook_frame_get_result_type", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeFrameGetResultType(nint frame);

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_hook_frame_get_result", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeFrameGetResult(nint frame, TideValue* value);

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_hook_frame_set_result", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeFrameSetResult(nint frame, TideValue* value);

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_hook_frame_cleanup", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeFrameCleanup(nint frame);

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_unhook", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeUnhook(long hookId);

    private static readonly IntPtr _loader = ProbeLoader();
    private static readonly bool _il2cppDetected = _loader != IntPtr.Zero && NativeIl2CppAvailable() != 0;

    private static IntPtr ProbeLoader()
    {
        try
        {
            return NativeLibrary.Load(LoaderDll);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>True when the loader is present in an IL2CPP game process (hooks possible).</summary>
    public static bool IsAvailable => _il2cppDetected;

    // ------------------------------------------------------------ registry

    private sealed class Site(GCHandle callbackHandle, bool retainAfterUnhook = false)
    {
        public long HookId;  // native id, assigned after a successful install
        public string Owner = "";
        public GCHandle CallbackHandle = callbackHandle;
        public GCHandle RootHandle;
        public bool RetainAfterUnhook { get; } = retainAfterUnhook;

        public void BindRoot(GCHandle root) => RootHandle = root;

        public void Free(bool force = false, bool freeRoot = true)
        {
            if (RetainAfterUnhook && !force)
            {
                return;
            }

            if (CallbackHandle.IsAllocated)
            {
                CallbackHandle.Free();
            }
            if (freeRoot && RootHandle.IsAllocated)
            {
                RootHandle.Free();
                RootHandle = default;
            }
        }
    }

    private sealed class TypedCallbacks
    {
        public Il2CppTypedHookPrefixCallback? Prefix;
        public Il2CppTypedHookPostfixCallback? Postfix;
    }

    private static readonly Lock RegistryLock = new();
    private static readonly Dictionary<long, Site> Sites = new();

    // The managed dispatch invoked by the native stub: (user_handle, args, arg_count) ->
    // skip?1:0. Resolved to a native address and passed to nami_il2cpp_hook.
    [UnmanagedCallersOnly]
    private static int Dispatch(nint userHandle, nint* args, int argCount)
    {
        if (userHandle == IntPtr.Zero)
        {
            return 0;
        }

        var site = (Site)GCHandle.FromIntPtr(userHandle).Target!;
        try
        {
            if (site.CallbackHandle.Target is not Il2CppHookCallback callback)
            {
                return 0;
            }

            var instance = argCount >= 1 ? args[0] : 0;
            return callback(instance, args, argCount) ? 1 : 0;
        }
        catch
        {
            // A throwing user callback must not corrupt the native dispatch path; the
            // game must not die because a patch callback threw.
            return 0;
        }
    }

    // Holds the full-path callbacks strongly so the GC never collects a delegate the
    // native stub can still call. One GCHandle keeps the holder (and through it both
    // delegates) alive for the hook's lifetime.
    private sealed class FullCallbacks
    {
        public Il2CppHookPrefixCallback? Prefix;
        public Il2CppHookPostfixCallback? Postfix;
    }

    // Full-path native ABI: (user_handle, args, arg_count, result_slot, return_kind) -
    // see native_stub.h. The fast-path Dispatch above has a 3-argument ABI, so these
    // are separate entries; both route through the same Site registry.
    [UnmanagedCallersOnly]
    private static int DispatchFullPrefix(nint userHandle, nint* args, int argCount,
        nint* result, int returnKind)
    {
        if (userHandle == IntPtr.Zero)
        {
            return 0;
        }

        var site = (Site)GCHandle.FromIntPtr(userHandle).Target!;
        try
        {
            if (site.CallbackHandle.Target is FullCallbacks { Prefix: { } prefix })
            {
                var instance = argCount >= 1 ? args[0] : 0;
                return prefix(instance, args, argCount, result, (Il2CppReturnKind)returnKind)
                    ? 1
                    : 0;
            }
        }
        catch
        {
            // Same containment as the fast path: a throwing user callback must not
            // corrupt the native dispatch or kill the game.
        }

        return 0;
    }

    [UnmanagedCallersOnly]
    private static void DispatchFullPostfix(nint userHandle, nint* args, int argCount,
        nint* result, int returnKind)
    {
        if (userHandle == IntPtr.Zero)
        {
            return;
        }

        var site = (Site)GCHandle.FromIntPtr(userHandle).Target!;
        try
        {
            if (site.CallbackHandle.Target is FullCallbacks { Postfix: { } postfix })
            {
                var instance = argCount >= 1 ? args[0] : 0;
                postfix(instance, args, argCount, result, (Il2CppReturnKind)returnKind);
            }
        }
        catch
        {
            // Same containment as the fast path.
        }
    }

    [UnmanagedCallersOnly]
    private static int DispatchTypedPrefix(nint userHandle, nint frame)
    {
        if (userHandle == IntPtr.Zero)
        {
            NativeFrameCleanup(frame);
            return 0;
        }

        var site = (Site)GCHandle.FromIntPtr(userHandle).Target!;
        var skip = false;
        Il2CppHookContext? context = null;
        try
        {
            if (site.CallbackHandle.Target is TypedCallbacks { Prefix: { } prefix })
            {
                context = new Il2CppHookContext(frame);
                skip = prefix(context);
            }
        }
        catch
        {
            // A typed callback is user code; a throw must leave the original call intact.
            skip = false;
        }
        finally
        {
            context?.Invalidate();
            if (skip)
            {
                NativeFrameCleanup(frame);
            }
        }

        return skip ? 1 : 0;
    }

    [UnmanagedCallersOnly]
    private static void DispatchTypedPostfix(nint userHandle, nint frame)
    {
        if (userHandle == IntPtr.Zero)
        {
            NativeFrameCleanup(frame);
            return;
        }

        var site = (Site)GCHandle.FromIntPtr(userHandle).Target!;
        try
        {
            if (site.CallbackHandle.Target is TypedCallbacks { Postfix: { } postfix })
            {
                using var context = new Il2CppHookContext(frame);
                postfix(context);
            }
        }
        catch
        {
            // A typed callback must not corrupt the native return path.
        }
        finally
        {
            NativeFrameCleanup(frame);
        }
    }

    private static readonly IntPtr DispatchAddress = ResolveDispatch();
    private static readonly IntPtr DispatchFullPrefixAddress = ResolveDispatchFullPrefix();
    private static readonly IntPtr DispatchFullPostfixAddress = ResolveDispatchFullPostfix();

    private static IntPtr ResolveDispatch()
    {
        var addr = NativeInterop.GetCodeAddress(typeof(WaveIl2Cpp).GetMethod(nameof(Dispatch),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
        return addr == IntPtr.Zero ? IntPtr.Zero : addr;
    }

    private static IntPtr ResolveDispatchFullPrefix()
    {
        var addr = NativeInterop.GetCodeAddress(
            typeof(WaveIl2Cpp).GetMethod(nameof(DispatchFullPrefix),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
        return addr == IntPtr.Zero ? IntPtr.Zero : addr;
    }

    private static IntPtr ResolveDispatchFullPostfix()
    {
        var addr = NativeInterop.GetCodeAddress(
            typeof(WaveIl2Cpp).GetMethod(nameof(DispatchFullPostfix),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
        return addr == IntPtr.Zero ? IntPtr.Zero : addr;
    }

    private static IntPtr ResolveDispatchTypedPrefix()
    {
        var addr = NativeInterop.GetCodeAddress(
            typeof(WaveIl2Cpp).GetMethod(nameof(DispatchTypedPrefix),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
        return addr == IntPtr.Zero ? IntPtr.Zero : addr;
    }

    private static IntPtr ResolveDispatchTypedPostfix()
    {
        var addr = NativeInterop.GetCodeAddress(
            typeof(WaveIl2Cpp).GetMethod(nameof(DispatchTypedPostfix),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
        return addr == IntPtr.Zero ? IntPtr.Zero : addr;
    }

    private static readonly IntPtr DispatchTypedPrefixAddress = ResolveDispatchTypedPrefix();
    private static readonly IntPtr DispatchTypedPostfixAddress = ResolveDispatchTypedPostfix();

    // ------------------------------------------------------------ public API

    /// <summary>
    /// Installs a dispatch-stub detour over an IL2CPP game method (resolved by
    /// assembly/namespace/class/method + arity). Every call of the method on the game
    /// main thread runs <paramref name="callback"/> first; return true to skip the
    /// original. Throws <see cref="Il2CppHookException"/> on failure - never corrupts.
    /// </summary>
    public static Il2CppHook Hook(string assembly, string ns, string klass, string method,
        int argCount, Il2CppHookCallback callback, string owner)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(klass);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(owner);
        if (argCount < 0 || argCount > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(argCount),
                "fast-path hooks expose up to 4 register arguments; use HookFull for stack args / results");
        }

        if (!IsAvailable)
        {
            throw new Il2CppHookException(
                "Wave IL2CPP patching requires the Nami loader in an IL2CPP game process " +
                "(GameAssembly.dll present)");
        }

        if (DispatchAddress == IntPtr.Zero)
        {
            throw new Il2CppHookException("cannot resolve the Wave IL2CPP dispatch entry");
        }

        var siteRecord = new Site(GCHandle.Alloc(callback)) { Owner = owner };
        var siteHandle = GCHandle.Alloc(siteRecord, GCHandleType.Normal);
        siteRecord.BindRoot(siteHandle);
        var target = $"{ns}.{klass}::{method}({argCount})";

        var a = ZeroTerminated(assembly, 159);
        var n = ZeroTerminated(ns, 159);
        var k = ZeroTerminated(klass, 159);
        var m = ZeroTerminated(method, 159);
        try
        {
            fixed (byte* pa = a)
            fixed (byte* pn = n)
            fixed (byte* pk = k)
            fixed (byte* pm = m)
            {
                var rc = NativeHook(pa, pn, pk, pm, argCount, DispatchAddress, 0, 0,
                    GCHandle.ToIntPtr(siteHandle), out _, out var hookId);
                if (rc != 0)
                {
                    throw new Il2CppHookException(rc == -3
                        ? $"IL2CPP hook of {target} failed: main-thread executor unavailable " +
                          "(no game window yet? retry after the window exists / Tide.EnsureReady())"
                        : $"IL2CPP hook of {target} failed (code {rc}); see nami/native/nami-tide.log");
                }

                var site = (Site)siteHandle.Target!;
                site.HookId = hookId;
                lock (RegistryLock)
                {
                    Sites[hookId] = site;
                }

                return new Il2CppHook(owner, target, hookId);
            }
        }
        catch
        {
            if (siteHandle.Target is Site s)
            {
                s.Free(force: true, freeRoot: false);
            }

            siteHandle.Free();
            throw;
        }
    }

    /// <summary>
    /// Installs a FULL-PATH dispatch-stub detour (results + stack args): the original
    /// method runs between <paramref name="prefix"/> (return true to skip - optionally
    /// after writing a replacement into the result slot) and <paramref name="postfix"/>
    /// (may rewrite the result the caller receives). All <paramref name="argCount"/>
    /// arguments are exposed as contiguous raw slots (up to 12: registers first, then
    /// the caller's stack args); <paramref name="result"/> in both callbacks is 2 slots
    /// - [0] = rax bits, [1] = xmm0 bits - interpreted per <paramref name="returnKind"/>
    /// (write/read the rax slot for I32/I64/Void, the xmm0 slot for F32/F64).
    /// At least one of <paramref name="prefix"/> / <paramref name="postfix"/> must be
    /// supplied. Throws <see cref="Il2CppHookException"/> on failure - never corrupts.
    /// </summary>
    public static Il2CppHook HookFull(string assembly, string ns, string klass, string method,
        int argCount, Il2CppReturnKind returnKind, Il2CppHookPrefixCallback? prefix,
        Il2CppHookPostfixCallback? postfix, string owner)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(klass);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(owner);
        if (prefix is null && postfix is null)
        {
            throw new ArgumentException("at least one of prefix/postfix must be supplied", nameof(prefix));
        }

        if (argCount < 0 || argCount > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(argCount),
                "HookFull exposes up to 12 arguments (4 register + 8 stack)");
        }

        if (returnKind is < Il2CppReturnKind.Void or > Il2CppReturnKind.F64)
        {
            throw new ArgumentOutOfRangeException(nameof(returnKind));
        }

        if (!IsAvailable)
        {
            throw new Il2CppHookException(
                "Wave IL2CPP patching requires the Nami loader in an IL2CPP game process " +
                "(GameAssembly.dll present)");
        }

        if (DispatchFullPrefixAddress == IntPtr.Zero || DispatchFullPostfixAddress == IntPtr.Zero)
        {
            throw new Il2CppHookException("cannot resolve the Wave IL2CPP full-path dispatch entries");
        }

        var cb = new FullCallbacks { Prefix = prefix, Postfix = postfix };
        var siteRecord = new Site(GCHandle.Alloc(cb)) { Owner = owner };
        var siteHandle = GCHandle.Alloc(siteRecord, GCHandleType.Normal);
        siteRecord.BindRoot(siteHandle);
        var target = $"{ns}.{klass}::{method}({argCount})";

        var a = ZeroTerminated(assembly, 159);
        var n = ZeroTerminated(ns, 159);
        var k = ZeroTerminated(klass, 159);
        var m = ZeroTerminated(method, 159);
        try
        {
            fixed (byte* pa = a)
            fixed (byte* pn = n)
            fixed (byte* pk = k)
            fixed (byte* pm = m)
            {
                var rc = NativeHook(pa, pn, pk, pm, argCount, DispatchFullPrefixAddress,
                    DispatchFullPostfixAddress, (int)returnKind, GCHandle.ToIntPtr(siteHandle),
                    out _, out var hookId);
                if (rc != 0)
                {
                    throw new Il2CppHookException(rc == -3
                        ? $"IL2CPP hook of {target} failed: main-thread executor unavailable " +
                          "(no game window yet? retry after the window exists / Tide.EnsureReady())"
                        : $"IL2CPP hook of {target} failed (code {rc}); see nami/native/nami-tide.log");
                }

                var site = (Site)siteHandle.Target!;
                site.HookId = hookId;
                lock (RegistryLock)
                {
                    Sites[hookId] = site;
                }

                return new Il2CppHook(owner, target, hookId);
            }
        }
        catch
        {
            if (siteHandle.Target is Site s)
            {
                s.Free(force: true, freeRoot: false);
            }

            siteHandle.Free();
            throw;
        }
    }

    /// <summary>
    /// Installs a typed IL2CPP full-path hook. Parameter and return metadata are resolved on
    /// the game main thread; unsupported structs, ref/out values, hidden returns, and
    /// ambiguous overloads are refused before the detour is installed. The
    /// <paramref name="parameterTypes"/> array is the exact user-parameter TideType shape;
    /// an empty array selects a zero-parameter method. The native resolver requires the
    /// explicit shape to avoid guessing hidden IL2CPP ABI slots.
    /// </summary>
    public static Il2CppHook HookTyped(string assembly, string ns, string klass, string method,
        IReadOnlyList<TideType> parameterTypes, TideType? returnType,
        Il2CppTypedHookPrefixCallback? prefix, Il2CppTypedHookPostfixCallback? postfix, string owner)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(klass);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(parameterTypes);
        ArgumentNullException.ThrowIfNull(owner);
        if (prefix is null && postfix is null)
        {
            throw new ArgumentException("at least one of prefix/postfix must be supplied", nameof(prefix));
        }
        if (parameterTypes.Count > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(parameterTypes), "typed hooks support at most 12 user arguments");
        }
        if (DispatchTypedPrefixAddress == IntPtr.Zero || DispatchTypedPostfixAddress == IntPtr.Zero)
        {
            throw new Il2CppHookException("cannot resolve the Wave IL2CPP typed dispatch entries");
        }
        if (!IsAvailable)
        {
            throw new Il2CppHookException(
                "Wave IL2CPP patching requires the Nami loader in an IL2CPP game process " +
                "(GameAssembly.dll present)");
        }

        var callbacks = new TypedCallbacks { Prefix = prefix, Postfix = postfix };
        var siteRecord = new Site(GCHandle.Alloc(callbacks), retainAfterUnhook: true) { Owner = owner };
        var siteHandle = GCHandle.Alloc(siteRecord, GCHandleType.Normal);
        siteRecord.BindRoot(siteHandle);
        var target = $"{ns}.{klass}::{method}({parameterTypes.Count}) [typed]";
        var a = ZeroTerminated(assembly, 159);
        var n = ZeroTerminated(ns, 159);
        var k = ZeroTerminated(klass, 159);
        var m = ZeroTerminated(method, 159);
        var expected = parameterTypes.Select(type => (int)type).ToArray();
        try
        {
            fixed (byte* pa = a)
            fixed (byte* pn = n)
            fixed (byte* pk = k)
            fixed (byte* pm = m)
            fixed (int* pe = expected)
            {
                var expectedReturn = returnType.HasValue ? (int)returnType.Value : -1;
                var rc = NativeHookTyped(pa, pn, pk, pm, expected.Length, pe, expected.Length,
                    expectedReturn, DispatchTypedPrefixAddress, DispatchTypedPostfixAddress,
                    GCHandle.ToIntPtr(siteHandle), out var hookId);
                if (rc != 0)
                {
                    throw new Il2CppHookException(rc == -3
                        ? $"IL2CPP typed hook of {target} failed: main-thread executor unavailable"
                        : $"IL2CPP typed hook of {target} failed (code {rc}); see nami/native/nami-tide.log");
                }

                var site = (Site)siteHandle.Target!;
                site.HookId = hookId;
                lock (RegistryLock)
                {
                    Sites[hookId] = site;
                }
                return new Il2CppHook(owner, target, hookId);
            }
        }
        catch
        {
            if (siteHandle.Target is Site s) s.Free(force: true, freeRoot: false);
            siteHandle.Free();
            throw;
        }
    }

    /// <summary>Removes every hook installed by <paramref name="owner"/>.</summary>
    public static void UnhookAll(string owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        List<long> ids;
        lock (RegistryLock)
        {
            ids = Sites.Where(kv => kv.Value.Owner == owner).Select(kv => kv.Key).ToList();
        }

        foreach (var id in ids)
        {
            lock (RegistryLock)
            {
                if (!Sites.TryGetValue(id, out var site))
                {
                    continue;
                }

                Sites.Remove(id);
                NativeUnhook(site.HookId);
                site.Free();
            }
        }
    }

    private static byte[] ZeroTerminated(string s, int max)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s ?? string.Empty);
        var buf = new byte[bytes.Length + 1];
        if (bytes.Length > max)
        {
            bytes = bytes.AsSpan(0, max).ToArray();
        }

        Array.Copy(bytes, buf, bytes.Length);
        return buf;
    }
}