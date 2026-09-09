using System.Runtime.InteropServices;
using System.Text;

namespace Nami;

/// <summary>
/// Tide — the Nami ↔ game bridge.
///
/// Tide connects the Nami-hosted .NET runtime (CoreCLR) to the game's own managed runtime
/// (Unity Mono). CRITICAL ARCHITECTURE: no CoreCLR-managed thread may ever call into Mono
/// directly (CoreCLR's GC crashes), and foreign native threads crash Unity Mono's Boehm GC on
/// their first allocating call. Tide therefore executes every Mono call ON THE GAME'S MAIN
/// THREAD: nami_loader.dll hooks mono_runtime_invoke (called constantly by the game main
/// thread) and drains a queue of native ops inline. CoreCLR enqueues a typed op and blocks
/// until the main thread has run it.
///
/// Verified in-game across Unity 2022.3.x and Unity 6 Mono titles.
/// </summary>
public static unsafe partial class Tide
{
    /// <summary>Thrown when Tide cannot reach or execute against the game runtime.</summary>
    public sealed class TideException : Exception
    {
        public TideException(string message) : base(message) { }
        public TideException(string message, Exception inner) : base(message, inner) { }

        /// <summary>The native TideResult code (0 ok; -1 not found/invalid; -2 Mono exception; -3 pump).</summary>
        public int Code { get; init; }

        /// <summary>True when the failure was a Mono exception thrown by the game method.</summary>
        public bool IsMonoException => Code == -2;
    }

    private const string LoaderDll = "nami_loader";

    // Native ops implemented in native/loader/tide_ops.cpp, executed on the game main thread
    // (Mono backend) or native/loader/tide_il2cpp*.cpp (IL2CPP backend).
    [DllImport(LoaderDll, EntryPoint = "nami_tide_unity_log", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeUnityLog(byte* message);

    [DllImport(LoaderDll, EntryPoint = "nami_tide_invoke_static", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeInvokeStatic(byte* assembly, byte* ns, byte* klass, byte* method);

    [DllImport(LoaderDll, EntryPoint = "nami_tide_object_op", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NativeObjectOp(CallRequest* request);

    // Runs an op on the game main thread inside its window procedure (frame boundary —
    // zero invoke frames on the stack). Required for Unity scene-iteration APIs
    // (Object.FindObjectOfType), which abort inside any nested invoke (0xe0000001).
    [DllImport(LoaderDll, EntryPoint = "nami_tide_object_op_window", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NativeObjectOpWindow(CallRequest* request);

    // Batch execution: N requests, ONE main-thread round trip (Mono backend).
    [DllImport(LoaderDll, EntryPoint = "nami_tide_object_op_batch", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NativeObjectOpBatch(BatchRequest* batch);

    // Batch execution: N requests, ONE main-thread round trip (IL2CPP backend).
    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_object_op_batch", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NativeIl2CppObjectOpBatch(BatchRequest* batch);

    [DllImport(LoaderDll, EntryPoint = "nami_tide_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeFree(void* ptr);

    // IL2CPP backend exports (GameAssembly.dll titles).
    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_available", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NativeIl2CppAvailable();

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_install", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NativeIl2CppInstall();

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_object_op", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NativeIl2CppObjectOp(CallRequest* request);

    [DllImport(LoaderDll, EntryPoint = "nami_il2cpp_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeIl2CppFree(void* ptr);

    // The loader module handle (nami_loader.dll is loaded in-process).
    private static IntPtr _loaderModule;

    /// <summary>
    /// Backend the bridge is talking to. Mono = Unity Mono titles (classic Tide, drain via
    /// mono_runtime_invoke detour); Il2Cpp = GameAssembly.dll titles (drain via the game's
    /// main window proc). Determined once at first use.
    /// </summary>
    public enum Backend
    {
        Mono,
        Il2Cpp,
        None,
    }

    private static Backend _backend;
    private static int _backendProbed;

    /// <summary>The active game-runtime backend (Mono or IL2CPP), detected once.</summary>
    public static Backend ActiveBackend
    {
        get
        {
            if (Volatile.Read(ref _backendProbed) == 0)
            {
                lock (typeof(Tide))
                {
                    if (_backendProbed == 0)
                    {
                        _backend = IsAvailable && NativeIl2CppAvailable() != 0 ? Backend.Il2Cpp : Backend.Mono;
                        if (_backend == Backend.Il2Cpp)
                        {
                            // Install the IL2CPP main-thread executor (window-proc drain).
                            if (NativeIl2CppInstall() != 0)
                            {
                                NoteIl2CppInstalled();
                            }
                        }
                        Volatile.Write(ref _backendProbed, 1);
                    }
                }
            }
            return _backend;
        }
    }

    static Tide()
    {
        // nami_loader.dll is loaded in the game process by the injector (remote LoadLibraryW),
        // so loading by name resolves the already-loaded module.
        try
        {
            _loaderModule = NativeLibrary.Load(LoaderDll);
        }
        catch
        {
            _loaderModule = IntPtr.Zero;
        }
    }

    /// <summary>True when the loader (and thus the Tide main-thread drain) is present.</summary>
    public static bool IsAvailable => _loaderModule != IntPtr.Zero;

    /// <summary>
    /// True when the backend's main-thread executor is installed and ops can run.
    /// On Mono this is true as soon as the loader is present; on IL2CPP it requires the
    /// game's main window to exist (the executor drains the window proc), so it can be
    /// false during early boot.
    /// </summary>
    public static bool IsReady => ActiveBackend == Backend.Mono ||
                                  (ActiveBackend == Backend.Il2Cpp && _il2cppInstalled == 1);

    private static int _il2cppInstalled;

    internal static void NoteIl2CppInstalled() => Volatile.Write(ref _il2cppInstalled, 1);

    /// <summary>
    /// Re-attempts backend executor installation (IL2CPP only). The executor needs the
    /// game's main window, which may not exist when mods load; call this periodically until
    /// <see cref="IsReady"/> is true before issuing ops on IL2CPP titles.
    /// </summary>
    public static bool EnsureReady()
    {
        if (IsReady)
        {
            return true;
        }
        if (ActiveBackend == Backend.Mono)
        {
            return IsAvailable;
        }
        if (ActiveBackend == Backend.Il2Cpp && _il2cppInstalled == 0)
        {
            if (NativeIl2CppInstall() != 0)
            {
                NoteIl2CppInstalled();
            }
        }
        return IsReady;
    }

    internal static void NativeFreeFor(Backend backend, void* ptr)
    {
        if (backend == Backend.Il2Cpp)
        {
            NativeIl2CppFree(ptr);
        }
        else
        {
            NativeFree(ptr);
        }
    }

    private static byte[] Ansi(string s, int max)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
        if (bytes.Length > max)
        {
            bytes = bytes.AsSpan(0, max).ToArray();
        }

        return bytes;
    }

    /// <summary>Invokes <c>UnityEngine.Debug.Log(object)</c> on the game main thread.</summary>
    public static bool UnityLog(string message)
    {
        if (!IsAvailable)
        {
            return false;
        }

        // IL2CPP has no NativeUnityLog export: route through the typed call op, which
        // the IL2CPP backend supports (its Debug.Log(object) single-arg boxing case).
        if (ActiveBackend == Backend.Il2Cpp)
        {
            try
            {
                GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Debug")
                    .CallStatic("Log", TideValue.FromString(message));
                return true;
            }
            catch
            {
                return false;
            }
        }

        var bytes = Ansi(message, 511);
        fixed (byte* p = bytes)
        {
            var buf = stackalloc byte[512];
            for (int i = 0; i < bytes.Length; i++)
            {
                buf[i] = p[i];
            }

            buf[bytes.Length] = 0;
            return NativeUnityLog(buf) == 0;
        }
    }

    /// <summary>
    /// Invokes a parameterless static method on a game class, on the game main thread.
    /// <paramref name="assembly"/> is the assembly name (with or without .dll).
    /// Returns true if the method ran without a game exception.
    /// </summary>
    public static bool InvokeStatic(string assembly, string ns, string klass, string method)
    {
        if (!IsAvailable)
        {
            return false;
        }

        // IL2CPP has no NativeInvokeStatic export: same call through the typed op.
        if (ActiveBackend == Backend.Il2Cpp)
        {
            try
            {
                GameClass.Resolve(assembly, ns, klass).CallStatic(method);
                return true;
            }
            catch
            {
                return false;
            }
        }

        var a = Ansi(assembly, 127);
        var n = Ansi(ns, 127);
        var k = Ansi(klass, 127);
        var m = Ansi(method, 127);

        fixed (byte* pa = a)
        fixed (byte* pn = n)
        fixed (byte* pk = k)
        fixed (byte* pm = m)
        {
            var ba = stackalloc byte[128];
            var bn = stackalloc byte[128];
            var bk = stackalloc byte[128];
            var bm = stackalloc byte[128];
            CopyZ(ba, pa, a.Length);
            CopyZ(bn, pn, n.Length);
            CopyZ(bk, pk, k.Length);
            CopyZ(bm, pm, m.Length);
            return NativeInvokeStatic(ba, bn, bk, bm) == 0;
        }
    }

    private static unsafe void CopyZ(byte* dst, byte* src, int len)
    {
        for (int i = 0; i < len; i++)
        {
            dst[i] = src[i];
        }

        dst[len] = 0;
    }
}

/// <summary>Core marshaling for typed game access (see GameClass / GameObject).</summary>
internal static unsafe class TideObjectOp
{
    private static void Fill(byte* dst, int capacity, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
        int n = Math.Min(bytes.Length, capacity - 1);
        for (int i = 0; i < n; i++)
        {
            dst[i] = bytes[i];
        }

        dst[n] = 0;
    }

    /// <summary>
    /// Runs a class-targeted op (static field/method, new object). Any string ARG buffers
    /// are freed after the call (they are only needed during it). String RETURNS must be
    /// freed by the caller via TideValue.FreeNativeReturn after reading.
    /// </summary>
    public static TideValue Call(TideCallOp op, GameClass target, string member,
        TideValue* args, int argCount, TideType returnType, bool window = false)
    {
        try
        {
            var req = new CallRequest();
            Fill(req.Assembly, 160, target.Assembly);
            Fill(req.Ns, 160, target.Namespace);
            Fill(req.Klass, 160, target.Name);
            Fill(req.Member, 160, member);
            req.Op = op;
            req.ArgCount = argCount;
            req.HandleCapacity = 64;

            TideValue ret = default;
            ret.Type = returnType;
            req.Args = args;
            req.Ret = returnType == TideType.Void ? null : &ret;

            var backend = Tide.ActiveBackend;
            var timed = Nami.Sdk.TideMetrics.HasSink;
            var sw = timed ? System.Diagnostics.Stopwatch.StartNew() : null;
            var rc = RunOp(backend, window, &req);
            if (sw is not null)
            {
                sw.Stop();
                Nami.Sdk.TideMetrics.Record(sw.Elapsed.TotalMilliseconds);
            }

            if (rc != 0)
            {
                throw Error(op, $"{target.Name}.{member}", rc, req);
            }

            return ret;
        }
        finally
        {
            FreeArgStrings(args, argCount);
        }
    }

    /// <summary>Runs an instance-targeted op (instance field/method/free).</summary>
    public static TideValue CallInstance(TideCallOp op, long handle, string member,
        TideValue* args, int argCount, TideType returnType)
    {
        try
        {
            var req = new CallRequest();
            Fill(req.Member, 160, member);
            req.Op = op;
            req.ArgCount = argCount;
            req.HandleCapacity = 64;

            TideValue ret = default;
            ret.Type = returnType;
            req.Args = args;
            req.Ret = returnType == TideType.Void ? null : &ret;

            var backend = Tide.ActiveBackend;
            var timed = Nami.Sdk.TideMetrics.HasSink;
            var sw = timed ? System.Diagnostics.Stopwatch.StartNew() : null;
            var rc = RunOp(backend, false, &req);
            if (sw is not null)
            {
                sw.Stop();
                Nami.Sdk.TideMetrics.Record(sw.Elapsed.TotalMilliseconds);
            }

            if (rc != 0)
            {
                throw Error(op, member, rc, req);
            }

            return ret;
        }
        finally
        {
            FreeArgStrings(args, argCount);
        }
    }

    /// <summary>Dispatches a CallRequest to the active backend's native op export.</summary>
    private static int RunOp(Tide.Backend backend, bool window, CallRequest* req)
    {
        if (backend == Tide.Backend.Il2Cpp)
        {
            // The IL2CPP executor runs ops on the main thread inside the window proc; there
            // is no nested runtime_invoke frame, so the window distinction is a no-op
            // kept for ABI compatibility.
            return Tide.NativeIl2CppObjectOp(req);
        }
        return window ? Tide.NativeObjectOpWindow(req) : Tide.NativeObjectOp(req);
    }

    private static string ErrorMessage(CallRequest* req)
    {
        byte* p = req->ErrorMessage;
        int len = 0;
        while (len < 512 && p[len] != 0)
        {
            len++;
        }

        return len > 0 ? Encoding.UTF8.GetString(p, len) : string.Empty;
    }

    private static Tide.TideException Error(TideCallOp op, string member, int rc, CallRequest req)
    {
        var detail = ErrorMessage(&req);
        var reason = rc == -2
            ? "the game method threw a Mono exception"
            : $"code {rc}";
        var msg = $"Tide op {op} on {member} failed ({reason})" +
                  (detail.Length > 0 ? $": {detail}" : string.Empty);
        return new Tide.TideException(msg) { Code = rc };
    }

    private static void FreeArgStrings(TideValue* args, int argCount)
    {
        for (int i = 0; i < argCount; i++)
        {
            if (args[i].Type == TideType.String && args[i].Data.Str.Utf8 != null)
            {
                // String args were allocated with AllocHGlobal by TideValue.FromString.
                Marshal.FreeHGlobal((IntPtr)args[i].Data.Str.Utf8);
                args[i].Data.Str.Utf8 = null;
            }
        }
    }
}
