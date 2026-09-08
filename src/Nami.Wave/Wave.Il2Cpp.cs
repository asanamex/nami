using System.Runtime.InteropServices;
using Nami.Wave.Internal;

namespace Nami.Wave;

/// <summary>
/// IL2CPP method patching — Wave's game-side engine (the IL-copy engine, Wave.Patch,
/// works on CoreCLR methods only; IL2CPP methods are native x64 code in GameAssembly.dll).
///
/// v1 scope (honest): prefix observer + skip semantics. The callback receives the RAW
/// x64 argument registers (rcx/rdx/r8/r9) as pointers — for instance methods args[0] is
/// <c>this</c> (an Il2CppObject*), for statics args[0] is the first parameter; stack
/// arguments (5+) are not exposed. Returning <c>true</c> skips the original (the skip
/// return value is 0 — value-typed returns are not observable yet). The dispatch runs on
/// the game's main thread (window-proc executor), so callbacks must not block.
///
/// Install happens on the game main thread (il2cpp resolution is only safe there); the
/// game window must exist — on IL2CPP titles call <c>Tide.EnsureReady()</c> first.
/// </summary>
public static unsafe class WaveIl2Cpp
{
    /// <summary>
    /// The patch callback. <paramref name="instance"/> is args[0] (an Il2CppObject* for
    /// instance methods, else the first parameter). <paramref name="args"/> points at the
    /// 4 raw argument-register slots (rcx/rdx/r8/r9); only <paramref name="argCount"/>
    /// are meaningful. Return true to SKIP the original method.
    /// </summary>
    public delegate bool Il2CppHookCallback(nint instance, nint* args, int argCount);

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
        int argc, nint dispatch, nint userHandle, out nint trampoline, out long hookId);

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

    private static readonly IntPtr DispatchAddress = ResolveDispatch();

    private static IntPtr ResolveDispatch()
    {
        var addr = NativeInterop.GetCodeAddress(typeof(WaveIl2Cpp).GetMethod(nameof(Dispatch),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
        return addr == IntPtr.Zero ? IntPtr.Zero : addr;
    }

    // ------------------------------------------------------------ public API

    /// <summary>
    /// Installs a dispatch-stub detour over an IL2CPP game method (resolved by
    /// assembly/namespace/class/method + arity). Every call of the method on the game
    /// main thread runs <paramref name="callback"/> first; return true to skip the
    /// original. Throws <see cref="Il2CppHookException"/> on failure — never corrupts.
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
            throw new ArgumentOutOfRangeException(nameof(argCount), "v1 exposes up to 4 register arguments (stack args are not marshaled yet)");
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
                var rc = NativeHook(pa, pn, pk, pm, argCount, DispatchAddress,
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