using System.Runtime.InteropServices;
using Nami.Core.Logging;

namespace Nami.Runtime;

/// <summary>
/// Unmanaged entry point invoked by the native loader through CoreCLR's
/// load_assembly_and_get_function_pointer hosting API.
/// </summary>
public static unsafe class ComponentEntry
{
    /// <summary>Argument blob written by the native loader (native/core/runtime_host.cpp).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BootArgs
    {
        public const int NamiRootCapacity = 260;  // MAX_PATH (wchar_t)
        public fixed ushort NamiRoot[NamiRootCapacity];  // UTF-16, null-terminated
        public IntPtr MonoModule;
    }

    [UnmanagedCallersOnly(EntryPoint = "Nami_ComponentEntryPoint")]
    public static int EntryPoint(IntPtr arg, int sizeBytes)
    {
        try
        {
            if (arg == IntPtr.Zero || sizeBytes < sizeof(BootArgs))
            {
                return unchecked((int)0x80008081);  // invalid args
            }

            var args = *(BootArgs*)arg.ToPointer();
            var root = new string((char*)args.NamiRoot);

            Boot.Run(root, args.MonoModule);
            return 0;  // Boot.Run never returns
        }
        catch (Exception ex)
        {
            try
            {
                var logDir = Path.Combine(Path.GetTempPath(), "nami");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "component-error.log"),
                    $"[{DateTime.UtcNow:o}] {ex}\n");
            }
            catch
            {
                // nowhere left to log
            }

            return unchecked((int)0x80004005);  // E_FAIL
        }
    }
}
