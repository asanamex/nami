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

    // Native ops implemented in native/loader/tide_ops.cpp, executed on the game main thread.
    [DllImport(LoaderDll, EntryPoint = "nami_tide_unity_log", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeUnityLog(byte* message);

    [DllImport(LoaderDll, EntryPoint = "nami_tide_invoke_static", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeInvokeStatic(byte* assembly, byte* ns, byte* klass, byte* method);

    [DllImport(LoaderDll, EntryPoint = "nami_tide_object_op", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NativeObjectOp(CallRequest* request);

    // Runs an op AFTER the current mono_runtime_invoke returns (outside the nested frame).
    // Required for Unity scene-iteration APIs (Object.FindObjectOfType).
    [DllImport(LoaderDll, EntryPoint = "nami_tide_object_op_post", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NativeObjectOpPost(CallRequest* request);

    [DllImport(LoaderDll, EntryPoint = "nami_tide_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeFree(void* ptr);

    // The loader module handle (nami_loader.dll is loaded in-process).
    private static IntPtr _loaderModule;

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
    /// Returns true if the method ran without a Mono exception.
    /// </summary>
    public static bool InvokeStatic(string assembly, string ns, string klass, string method)
    {
        if (!IsAvailable)
        {
            return false;
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
        TideValue* args, int argCount, TideType returnType, bool postInvoke = false)
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

            var rc = postInvoke ? Tide.NativeObjectOpPost(&req) : Tide.NativeObjectOp(&req);
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

            var rc = Tide.NativeObjectOp(&req);
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
