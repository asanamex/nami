using System.Runtime.InteropServices;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Runtime;

/// <summary>
/// Bridges from the Nami-hosted CoreCLR into the game's native Mono runtime.
///
/// BepInEx-Mono plugins execute inside the game's Mono; Nami instead hosts its own modern
/// runtime and uses Mono's embedding API (resolved as raw function pointers) to reach game
/// objects. This is the Mono analogue of Il2CppInterop — call it "reverse interop".
/// </summary>
public static unsafe class MonoBridge
{
    // Mono embedding API (stable across Unity's libmono for the 2022.x era).
    // All string parameters marshal as ANSI (LPStr) C strings — Mono's API is `const char*`.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr MonoGetRootDomain();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate void MonoThreadAttach(IntPtr domain);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr MonoAssemblyLoaded(IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr MonoImageGetAssembly(IntPtr assembly);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr MonoClassFromName(IntPtr image, IntPtr nameSpace, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr MonoClassGetMethodFromName(IntPtr klass, IntPtr name, int paramCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr MonoRuntimeInvoke(IntPtr method, IntPtr obj, void* args, IntPtr exc);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr MonoStringNew(IntPtr domain, IntPtr text);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    // Marshals a managed string to a native ANSI buffer (never freed; used only for
    // short-lived embedding API lookups at attach time).
    private static IntPtr Ansi(string s)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(s);
        var buf = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, buf, bytes.Length);
        Marshal.WriteByte(buf, bytes.Length, 0);
        return buf;
    }

    private static IntPtr _rootDomain;
    private static IntPtr _unityEngineImage;
    private static IntPtr _debugLogMethod;
    private static MonoStringNew? _stringNew;
    private static MonoRuntimeInvoke? _runtimeInvoke;
    private static LogHub? _log;

    public static bool Attach(IntPtr monoModule, LogHub hub)
    {
        _log = hub;

        // Mono is loaded in-process; grab its module base. If the caller already passed a
        // handle, use that; otherwise locate by the well-known module name.
        hub.Log("monobridge", LogLevel.Info, $"attaching with module={monoModule.ToInt64():X}");
        var module = monoModule != IntPtr.Zero ? monoModule : GetModuleHandleW("mono-2.0-bdwgc.dll");
        if (module == IntPtr.Zero)
        {
            module = GetModuleHandleW("mono.dll");
        }

        if (module == IntPtr.Zero)
        {
            hub.Log("monobridge", LogLevel.Error, "could not locate the game's mono module");
            return false;
        }

        hub.Log("monobridge", LogLevel.Info, $"mono module at {module.ToInt64():X}");
        IntPtr GetExport(string name)
        {
            var p = GetProcAddress(module, name);
            hub.Log("monobridge", LogLevel.Trace, $"export {name} = {p.ToInt64():X}");
            return p;
        }

        var getRootDomain = Marshal.GetDelegateForFunctionPointer<MonoGetRootDomain>(GetExport("mono_get_root_domain"));
        var threadAttach = Marshal.GetDelegateForFunctionPointer<MonoThreadAttach>(GetExport("mono_thread_attach"));
        _stringNew = Marshal.GetDelegateForFunctionPointer<MonoStringNew>(GetExport("mono_string_new"));
        _runtimeInvoke = Marshal.GetDelegateForFunctionPointer<MonoRuntimeInvoke>(GetExport("mono_runtime_invoke"));
        var assemblyLoaded = Marshal.GetDelegateForFunctionPointer<MonoAssemblyLoaded>(GetExport("mono_assembly_loaded"));
        var imageGetAssembly = Marshal.GetDelegateForFunctionPointer<MonoImageGetAssembly>(GetExport("mono_assembly_get_image"));
        var classFromName = Marshal.GetDelegateForFunctionPointer<MonoClassFromName>(GetExport("mono_class_from_name"));
        var getMethod = Marshal.GetDelegateForFunctionPointer<MonoClassGetMethodFromName>(GetExport("mono_class_get_method_from_name"));

        // Attach this (native-born) thread to the root domain so runtime calls are legal.
        hub.Log("monobridge", LogLevel.Info, "calling mono_get_root_domain...");
        _rootDomain = getRootDomain();
        if (_rootDomain == IntPtr.Zero)
        {
            hub.Log("monobridge", LogLevel.Error, "mono_get_root_domain returned null (is the mono runtime up?)");
            return false;
        }

        hub.Log("monobridge", LogLevel.Info, $"root domain = {_rootDomain.ToInt64():X}, attaching thread");
        threadAttach(_rootDomain);
        hub.Log("monobridge", LogLevel.Info, "thread attached to mono domain");

        // UnityEngine.CoreModule.dll is already loaded by the game; fetch its image.
        var coreModule = assemblyLoaded(Ansi("UnityEngine.CoreModule.dll"));
        if (coreModule == IntPtr.Zero)
        {
            hub.Log("monobridge", LogLevel.Error, "UnityEngine.CoreModule.dll not found in mono");
            return false;
        }

        _unityEngineImage = imageGetAssembly(coreModule);
        var debugClass = classFromName(_unityEngineImage, Ansi("UnityEngine"), Ansi("Debug"));
        _debugLogMethod = getMethod(debugClass, Ansi("Log"), 1);

        if (_debugLogMethod == IntPtr.Zero)
        {
            hub.Log("monobridge", LogLevel.Error, "failed to resolve UnityEngine.Debug.Log(object)");
            return false;
        }

        hub.Log("monobridge", LogLevel.Info, "mono bridge attached: UnityEngine.Debug.Log(object) resolved");
        return true;
    }

    /// <summary>Invokes <c>UnityEngine.Debug.Log(object)</c> on the game's Mono runtime from our CoreCLR.</summary>
    public static void Log(string message)
    {
        if (_debugLogMethod == IntPtr.Zero || _stringNew is null || _runtimeInvoke is null || _rootDomain == IntPtr.Zero)
        {
            _log?.Log("monobridge", LogLevel.Warn, $"drop message (bridge not ready): {message}");
            return;
        }

        var str = _stringNew(_rootDomain, Ansi(message));
        var args = stackalloc IntPtr[1];
        args[0] = str;

        // Debug.Log(object) is static; instance = null.
        _runtimeInvoke(_debugLogMethod, IntPtr.Zero, args, IntPtr.Zero);
    }
}
