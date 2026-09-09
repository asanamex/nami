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

    /// <summary>PAGE_EXECUTE_READ (0x20) - used when restoring patch-site protection.</summary>
    public const uint PageExecuteRead = 0x20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void* VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(void* lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr hProcess, void* lpBaseAddress, nuint dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualQuery(void* lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nuint dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public void* BaseAddress;
        public void* AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    /// <summary>
    /// True when <paramref name="p"/> points at committed, executable memory (a plausible
    /// JIT code address). Guards detour installation against stale/tiered-JIT-raced
    /// addresses: refuse loudly instead of faulting in VirtualProtect.
    /// </summary>
    public static bool IsExecutableCode(void* p)
    {
        if (p == null)
        {
            return false;
        }

        try
        {
            if (!VirtualQuery(p, out var mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION)))
            {
                return false;
            }

            if ((mbi.State & MemCommit) == 0)
            {
                return false;
            }

            uint prot = mbi.Protect & 0xFF;
            return prot is 0x10 or 0x20 or 0x30 or 0x40 or 0x50 or 0x60 or 0x70 or 0x80 or 0xA0 or 0xE0;
        }
        catch
        {
            return false;
        }
    }

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

    /// <summary>
    /// Allocates a writable executable buffer within ±2GB of <paramref name="target"/> (for
    /// rel32 trampolines), walking down first then up in 64KB steps. Only commits pages
    /// reported MEM_FREE - committing inside another allocator's reserved range corrupts
    /// its bookkeeping (e.g. the CLR code-heap reservations) and crashes later, elsewhere.
    /// Returns null when none is found - the caller falls back to the absolute-jump form
    /// or refuses.
    /// </summary>
    public static void* TryAllocExecutableNear(void* target, nuint size)
    {
        const long span = 0x7F000000L;
        const long step = 65536;
        var baseAddr = (long)target;
        for (int dir = -1; dir <= 1; dir += 2)
        {
            for (long n = 1; n <= 2048; n++)
            {
                var hint = baseAddr + dir * n * step;
                if (hint <= 0x10000)
                {
                    break;
                }

                var dist = hint > baseAddr ? hint - baseAddr : baseAddr - hint;
                if (dist >= span)
                {
                    continue;
                }

                if (!IsFreeRegion((void*)hint))
                {
                    continue;
                }

                var p = VirtualAlloc((void*)hint, size, MemCommit | MemReserve, PageReadWrite);
                if (p == null)
                {
                    continue;
                }

                var got = (long)p - baseAddr;
                if (got < -span || got >= span)
                {
                    VirtualFree(p, 0, MemRelease);
                    continue;
                }

                return p;
            }
        }

        return null;
    }

    private const uint MemFree = 0x10000;

    private static bool IsFreeRegion(void* p)
    {
        try
        {
            return VirtualQuery(p, out var mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION))
                && mbi.State == MemFree;
        }
        catch
        {
            return false;
        }
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

    /// <summary>Makes a code region writable (patch sites during install/uninstall).
    /// Uses EXECUTE_READWRITE, not READWRITE: the target page may share its 4K page with
    /// live JIT code - including this very call stack (Install → Emit*) - and a
    /// non-executable flip would DEP-fault on return. Transient RWX under the Wave lock.</summary>
    public static uint MakeWritable(void* p, nuint size)
    {
        if (!VirtualProtect(p, size, PageExecuteReadWrite, out var old))
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
