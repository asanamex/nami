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
        catch (ArgumentException)
        {
            return IntPtr.Zero; // open generic definition (MethodHandle rejects it)
        }
        catch (NotSupportedException)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Follows a bounded chain of leading unconditional jumps (E9 rel32, FF 25 disp32) from an
    /// entry point to the real code. Stops at the first non-jump byte. The landing spot must
    /// itself be committed executable code - stub chains that resolve into unmapped memory
    /// (stale tiered-JIT entries, unpopulated slots) yield Zero instead of a faulting address.
    /// </summary>
    private static IntPtr FollowJumpStubs(IntPtr entry)
    {
        var p = (byte*)entry;
        if (!IsExecutable(p)) {
            return IntPtr.Zero;
        }
        for (int hops = 0; hops < 8; hops++)
        {
            byte b0 = p[0];
            if (b0 == 0xE9)
            {
                // jmp rel32 - target = p + 5 + rel32
                int rel = *(int*)(p + 1);
                p = p + 5 + rel;
                if (!IsExecutable(p)) {
                    return IntPtr.Zero;
                }
                continue;
            }

            if (b0 == 0xFF && p[1] == 0x25)
            {
                // jmp qword ptr [rip+disp32]
                int disp = *(int*)(p + 2);
                var slot = (byte**)(p + 6 + disp);
                p = *slot;
                if (!IsExecutable(p)) {
                    return IntPtr.Zero;
                }
                continue;
            }

            break;
        }

        return (IntPtr)p;
    }

    private static bool IsExecutable(byte* p) => RawMemory.IsExecutableCode(p);

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
