using System.Runtime.InteropServices;

namespace Nami.Wave.Internal;

/// <summary>
/// Minimal Windows virtual-memory helpers for building and installing inline detours.
/// Code pages are allocated writable, written, then flipped to RX (W^X discipline).
/// </summary>
internal static unsafe class RawMemory
{
    private const uint PageNoAccess = 0x01;
    private const uint PageReadWrite = 0x04;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;

    /// <summary>PAGE_EXECUTE_READ (0x20) — used when restoring patch-site protection.</summary>
    public const uint PageExecuteRead = 0x20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void* VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(void* lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr hProcess, void* lpBaseAddress, nuint dwSize);

    private static readonly IntPtr CurrentProcess = new(-1);

    /// <summary>Allocates a writable executable buffer (for trampoline building).</summary>
    public static void* AllocExecutable(nuint size)
    {
        var p = VirtualAlloc(null, size, MemCommit | MemReserve, PageReadWrite);
        if (p == null)
        {
            throw new WaveHookException($"VirtualAlloc failed: {Marshal.GetLastPInvokeError():X}");
        }

        return p;
    }

    public static void FreeExecutable(void* p, nuint size)
    {
        if (p != null)
        {
            VirtualFree(p, size, MemRelease);
        }
    }

    /// <summary>Makes a code region executable-and-readable (trampolines after writing).</summary>
    public static void MakeExecutable(void* p, nuint size)
    {
        if (!VirtualProtect(p, size, PageExecuteRead, out _))
        {
            throw new WaveHookException($"VirtualProtect(RX) failed: {Marshal.GetLastPInvokeError():X}");
        }
    }

    /// <summary>Makes a code region writable (patch sites during install/uninstall).</summary>
    public static uint MakeWritable(void* p, nuint size)
    {
        if (!VirtualProtect(p, size, PageReadWrite, out var old))
        {
            throw new WaveHookException($"VirtualProtect(RW) failed at {(nint)p:X} size={size}: {Marshal.GetLastPInvokeError():X}");
        }

        return old;
    }

    public static void RestoreProtection(void* p, nuint size, uint oldProtect)
    {
        VirtualProtect(p, size, oldProtect, out _);
    }

    /// <summary>Flushes the instruction cache for a modified code region (no-op on x64 Windows, kept for clarity).</summary>
    public static void FlushCode(void* p, nuint size) => FlushInstructionCache(CurrentProcess, p, size);
}

/// <summary>Thrown when a hook cannot be installed, with a precise reason.</summary>
public sealed class WaveHookException(string message) : Exception(message);
