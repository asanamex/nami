using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nami.Wave.Internal;

/// <summary>
/// Native helpers for Wave: resolving the real code address of a managed method.
///
/// Modern .NET (tiered JIT) does not guarantee that
/// <see cref="RuntimeMethodHandle.GetFunctionPointer"/> points directly at the method body:
/// it returns the stable entry point, which may be a precode/jump stub (leading
/// <c>E9 rel32</c> or <c>FF 25 disp32</c>) that forwards to the current native code.
/// We force JIT with PrepareMethod and then follow a bounded chain of leading jumps to the
/// actual body before installing a detour.
/// </summary>
internal static unsafe class NativeInterop
{
    /// <summary>
    /// Resolves the executable body address for a closed method, or IntPtr.Zero if it cannot
    /// be resolved safely. Forces JIT first, then follows leading jump stubs (max 8).
    /// </summary>
    public static IntPtr GetCodeAddress(MethodBase method)
    {
        try
        {
            var handle = method.MethodHandle;
            RuntimeHelpers.PrepareMethod(handle);
            var entry = handle.GetFunctionPointer();
            return FollowJumpStubs(entry);
        }
        catch (InvalidOperationException)
        {
            return IntPtr.Zero; // open generic / no code
        }
        catch (NotSupportedException)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Follows a bounded chain of leading unconditional jumps (E9 rel32, FF 25 disp32) from an
    /// entry point to the real code. Stops at the first non-jump byte.
    /// </summary>
    private static IntPtr FollowJumpStubs(IntPtr entry)
    {
        var p = (byte*)entry;
        for (int hops = 0; hops < 8; hops++)
        {
            byte b0 = p[0];
            if (b0 == 0xE9 && IsExecutable(p))
            {
                // jmp rel32 — target = p + 5 + rel32
                int rel = *(int*)(p + 1);
                p = p + 5 + rel;
                continue;
            }

            if (b0 == 0xFF && p[1] == 0x25 && IsExecutable(p))
            {
                // jmp qword ptr [rip+disp32]
                int disp = *(int*)(p + 2);
                var slot = (byte**)(p + 6 + disp);
                p = *slot;
                continue;
            }

            break;
        }

        return (IntPtr)p;
    }

    private static bool IsExecutable(byte* p)
    {
        try
        {
            if (VirtualQuery(p, out var mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == false)
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
}
