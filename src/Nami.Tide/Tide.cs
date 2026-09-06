using System.Runtime.InteropServices;
using System.Text;

namespace Nami.Tide;

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
/// Verified in-game (Unity 2022.3.27f1 Mono, Project Hardline): UnityLog executes
/// UnityEngine.Debug.Log on the game main thread with the game stable.
/// </summary>
public static unsafe partial class Tide
{
    /// <summary>Thrown when Tide cannot reach the game runtime.</summary>
    public sealed class TideException : Exception
    {
        public TideException(string message) : base(message) { }
        public TideException(string message, Exception inner) : base(message, inner) { }
    }

    private const string LoaderDll = "nami_loader";

    // Native ops implemented in native/loader/tide_ops.cpp, executed on the game main thread.
    [DllImport(LoaderDll, EntryPoint = "nami_tide_unity_log", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeUnityLog(byte* message);

    [DllImport(LoaderDll, EntryPoint = "nami_tide_invoke_static", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeInvokeStatic(byte* assembly, byte* ns, byte* klass, byte* method);

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
        var bytes = Encoding.ASCII.GetBytes(s ?? string.Empty);
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
