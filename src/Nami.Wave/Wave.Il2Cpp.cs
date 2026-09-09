using System.Runtime.InteropServices;
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

    private sealed class Site(GCHandle callbackHandle)
    {
        public long HookId;  // native id, assigned after a successful install
        public string Owner = "";
        public GCHandle CallbackHandle = callbackHandle;
        public void Free() => CallbackHandle.Free();
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

        var siteHandle = GCHandle.Alloc(
            new Site(GCHandle.Alloc(callback)) { Owner = owner }, GCHandleType.Normal);
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
                s.Free();
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
        var siteHandle = GCHandle.Alloc(
            new Site(GCHandle.Alloc(cb)) { Owner = owner }, GCHandleType.Normal);
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
                s.Free();
            }

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