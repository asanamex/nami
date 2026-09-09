using System.Reflection;
using System.Reflection.Emit;

namespace Nami.Wave;

// ponytail: one parameterized emitter covers blueprints B and D (spill mask + skip
// variant are data, not code paths). Blueprint C (XMM-only spill) is a future 4th flag
// combo - D serves those shapes today. Blueprint A stays in Wave.cs (BuildDispatcher,
// byte-identical incl. its framed skip trampoline).

/// <summary>Which engine serves a patched method.</summary>
public enum WavePatchEngine
{
    /// <summary>No patch installed.</summary>
    None,
    /// <summary>Native stub (blueprint A/B/D): prefix-only, original runs via tail-jump.</summary>
    Fast,
    /// <summary>IL-copy body with injected prefix/postfix chain.</summary>
    ILCopy,
}

/// <summary>
/// Fast-path analysis, hook invokers, and the B/D native stub emitter for the unified
/// <see cref="Wave.Patch"/> router. Soundness invariant (see docs/wave.md): the stub runs
/// outside JIT GC accounting, so a fast shape may only carry values the collector never
/// needs to track - reference-free primitives/enums/opaque pointers, never byref. The
/// stub therefore only ever spills and restores such values; anything else routes to M2,
/// which runs as real JIT-compiled code with proper GC maps.
/// </summary>
internal static unsafe class WaveFast
{
    internal enum SlotKind : byte { Int, Float }
    internal enum ReturnKind : byte { Void, Int, Float }

    /// <summary>
    /// A fast-eligible target shape. Blueprint A = no register args (today's M1 case);
    /// B = all-int args (spill GP bank only); D = anything else eligible (spill both banks).
    /// </summary>
    internal sealed class FastShape
    {
        public required SlotKind[] ParamKinds;
        public required ReturnKind Return;
        public required char Blueprint; // 'A', 'B' or 'D'

        public int Arity => ParamKinds.Length;
    }

    /// <summary>True for values the GC never tracks: primitives, opaque pointers, enums.</summary>
    internal static bool IsGcTrackingFree(Type t) =>
        t.IsPrimitive || t == typeof(IntPtr) || t == typeof(UIntPtr) || t.IsEnum;

    private static SlotKind? Classify(Type t)
    {
        if (t.IsByRef)
        {
            return null; // managed byref (incl. interior pointers) is GC-tracked
        }
        if (t == typeof(float) || t == typeof(double))
        {
            return SlotKind.Float;
        }
        return IsGcTrackingFree(t) ? SlotKind.Int : null;
    }

    /// <summary>
    /// Returns the fast shape for <paramref name="target"/>, or null when it must take
    /// the IL-copy path. Instance methods always fail: the receiver is a reference (class)
    /// or a managed pointer (struct) - both GC-tracked.
    /// </summary>
    internal static FastShape? AnalyzeShape(MethodBase target)
    {
        if (!target.IsStatic)
        {
            return null;
        }
        var ps = target.GetParameters();
        if (ps.Length > 4)
        {
            return null;
        }
        var kinds = new SlotKind[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var k = Classify(ps[i].ParameterType);
            if (k is null)
            {
                return null;
            }
            kinds[i] = k.Value;
        }
        ReturnKind ret;
        if (target is MethodInfo mi)
        {
            if (mi.ReturnType == typeof(void))
            {
                ret = ReturnKind.Void;
            }
            else
            {
                var k = Classify(mi.ReturnType);
                if (k is null)
                {
                    return null;
                }
                ret = k == SlotKind.Float ? ReturnKind.Float : ReturnKind.Int;
            }
        }
        else
        {
            ret = ReturnKind.Void; // constructors
        }
        char blueprint = kinds.Length == 0 ? 'A'
            : kinds.All(k => k == SlotKind.Int) ? 'B' : 'D';
        return new FastShape { ParamKinds = kinds, Return = ret, Blueprint = blueprint };
    }

    /// <summary>
    /// How one hook binds to a fast shape: which target slots feed its parameters
    /// (empty = blind), and whether it votes skip. Null = cannot bind (route to M2,
    /// which reports the precise convention error).
    /// </summary>
    internal sealed class FastHookBinding
    {
        public required int[] Slots;
        public required bool ReturnsVote;
        /// <summary>True for Hook gates (true = skip); false for Patch prefixes (false = skip).</summary>
        public required bool VoteIfTrue;
    }

    /// <summary>
    /// Binds <paramref name="hook"/> for the fast path: by-value parameters matched by
    /// name (a subset is fine), no __convention names, void/bool return for prefixes and
    /// void for postfixes. Anything else returns null (M2 handles it).
    /// </summary>
    internal static FastHookBinding? BindHook(
        Delegate hook, ParameterInfo[] targetParams, Type[] targetParamTypes, bool isPrefix)
    {
        var hookMethod = hook.Method;
        var hookParams = hookMethod.GetParameters();
        if (hookParams.Length == 0)
        {
            hookParams = hook.GetType().GetMethod("Invoke")!.GetParameters();
        }
        var invokeRet = hook.GetType().GetMethod("Invoke")!.ReturnType;
        if (isPrefix)
        {
            if (invokeRet != typeof(void) && invokeRet != typeof(bool))
            {
                return null;
            }
        }
        else if (invokeRet != typeof(void))
        {
            return null;
        }
        var slots = new int[hookParams.Length];
        for (int i = 0; i < hookParams.Length; i++)
        {
            var hp = hookParams[i];
            var name = hp.Name ?? "";
            if (name is "__instance" or "__result" or "__state" or "__args")
            {
                return null;
            }
            if (hp.ParameterType.IsByRef)
            {
                return null;
            }
            int idx = -1;
            for (int j = 0; j < targetParams.Length; j++)
            {
                if (targetParams[j].Name == name)
                {
                    idx = j;
                    break;
                }
            }
            if (idx < 0 || hp.ParameterType != targetParamTypes[idx])
            {
                return null;
            }
            slots[i] = idx;
        }
        return new FastHookBinding { Slots = slots, ReturnsVote = isPrefix && invokeRet == typeof(bool), VoteIfTrue = false };
    }

    internal delegate int FastPreInvoker(object hook, IntPtr block);

    /// <summary>
    /// Warms a hook invoker outside the stub window. First-call JIT compilation
    /// allocates and inspects the stack - fatal while a GC-info-less stub frame is
    /// live - so every callee in the stub-called subgraph must already be compiled
    /// before the stub is installed. Obtaining a function pointer requires compiled
    /// code, so this cannot silently skip. The pointer itself is discarded (calling a
    /// managed body through it would be the interop-convention dead end from wave.md);
    /// only its compilation side effect is wanted.
    /// </summary>
    internal static void WarmInvoker(FastPreInvoker invoker, Delegate hook)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(hook);
        _ = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(invoker);
    }

    /// <summary>
    /// Builds a per-hook invoker: loads the bound slots from the stub's spill block and
    /// calls the delegate's Invoke. Emitted once per hook at patch time; the hot path is
    /// a few loads and one callvirt, with no allocations.
    /// </summary>
    internal static FastPreInvoker MakeInvoker(
        Delegate hook, FastShape shape, FastHookBinding binding)
    {
        var hookType = hook.GetType();
        var invoke = hookType.GetMethod("Invoke")!;
        var hookParams = invoke.GetParameters();
        var dm = new DynamicMethod(
            $"Wave_FastPre_{hookType.Name}", typeof(int),
            new[] { typeof(object), typeof(IntPtr) },
            typeof(WaveFast).Module, skipVisibility: true);
        var il = dm.GetILGenerator();
        // Instance first: callvirt consumes [instance, arg1, ...] in push order.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, hookType);
        for (int i = 0; i < hookParams.Length; i++)
        {
            int slot = binding.Slots[i];
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4, slot * 8);
            il.Emit(OpCodes.Add);
            EmitSlotLoad(il, hookParams[i].ParameterType);
        }
        il.Emit(OpCodes.Callvirt, invoke);
        if (binding.ReturnsVote)
        {
            // Hook gate: true = skip. Patch prefix: false = skip.
            var skip = il.DefineLabel();
            if (binding.VoteIfTrue)
            {
                il.Emit(OpCodes.Brtrue, skip);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(skip);
                il.Emit(OpCodes.Ldc_I4_1);
            }
            else
            {
                il.Emit(OpCodes.Brtrue, skip);
                il.Emit(OpCodes.Ldc_I4_1); // false = skip
                il.Emit(OpCodes.Ret);
                il.MarkLabel(skip);
                il.Emit(OpCodes.Ldc_I4_0);
            }
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_0);
        }
        il.Emit(OpCodes.Ret);
        return (FastPreInvoker)dm.CreateDelegate(typeof(FastPreInvoker));
    }

    private static void EmitSlotLoad(ILGenerator il, Type t)
    {
        if (t.IsEnum)
        {
            t = Enum.GetUnderlyingType(t); // enums ride the stack as their underlying type
        }
        if (t == typeof(bool) || t == typeof(byte))
        {
            il.Emit(OpCodes.Ldind_U1);
        }
        else if (t == typeof(sbyte))
        {
            il.Emit(OpCodes.Ldind_I1);
        }
        else if (t == typeof(short) || t == typeof(char))
        {
            il.Emit(OpCodes.Ldind_I2);
        }
        else if (t == typeof(ushort))
        {
            il.Emit(OpCodes.Ldind_U2);
        }
        else if (t == typeof(int))
        {
            il.Emit(OpCodes.Ldind_I4);
        }
        else if (t == typeof(uint))
        {
            il.Emit(OpCodes.Ldind_U4);
        }
        else if (t == typeof(long) || t == typeof(ulong))
        {
            il.Emit(OpCodes.Ldind_I8);
        }
        else if (t == typeof(IntPtr) || t == typeof(UIntPtr))
        {
            il.Emit(OpCodes.Ldind_I); // native-int slot; signedness is a view, bits identical
        }
        else if (t == typeof(float))
        {
            il.Emit(OpCodes.Ldind_R4);
        }
        else if (t == typeof(double))
        {
            il.Emit(OpCodes.Ldind_R8);
        }
        else
        {
            throw new InvalidOperationException($"unexpected fast hook parameter type {t}");
        }
    }

    // ------------------------------------------------------------ B/D stub emitter
    //
    // Layout: sub rsp,FRAME; spill used reg slots to [rsp+32..]; rcx=site, rdx=block;
    // call pre; jnz SKIP; restore regs; add rsp,FRAME; jmp trampoline.
    // SKIP: add rsp,FRAME; default return; ret. The skip path restores nothing: the
    // ABI's caller-saved registers are dead to the caller across a call boundary, and
    // the original prologue never runs, so there is no frame to unwind (unlike A's
    // skip trampoline, which runs a relocated framed prologue).

    /// <summary>
    /// Builds a blueprint-B/D stub. When <paramref name="nearTarget"/> is non-zero the
    /// stub must live within ±2GB of it (near-jump site); failure throws rather than
    /// emitting an unreachable jump. Returns (stub, size).
    /// </summary>
    internal static (IntPtr Stub, nuint Size) BuildStub(
        FastShape shape, IntPtr siteHandle, IntPtr prePtr, IntPtr trampoline, IntPtr nearTarget)
    {
        if (shape.Blueprint == 'A')
        {
            throw new ArgumentException("blueprint A uses BuildDispatcher", nameof(shape));
        }
        int n = shape.Arity;
        // Frame must leave rsp % 16 == 8 at the dispatch call (callee then sees 0):
        // stub entry is post-call (rsp % 16 == 0), so the frame is 8 mod 16.
        int frame = 32 + 8 * n;
        if (frame % 16 == 0)
        {
            frame += 8;
        }
        var code = new byte[192];
        int o = 0;
        void B(byte b) => code[o++] = b;
        void D32(int v)
        {
            code[o++] = (byte)v;
            code[o++] = (byte)(v >> 8);
            code[o++] = (byte)(v >> 16);
            code[o++] = (byte)(v >> 24);
        }
        void U64(ulong v)
        {
            for (int i = 0; i < 8; i++)
            {
                code[o++] = (byte)(v >> (8 * i));
            }
        }

        B(0x48); B(0x83); B(0xEC); B((byte)frame); // sub rsp,FRAME
        for (int i = 0; i < n; i++)
        {
            int disp = 32 + 8 * i;
            if (shape.ParamKinds[i] == SlotKind.Int)
            {
                // mov [rsp+disp],GPR[i]
                B((byte)(i < 2 ? 0x48 : 0x4C)); B(0x89);
                B((byte)(i switch { 0 => 0x8C, 1 => 0x94, 2 => 0x84, _ => 0x8C }));
                B(0x24); D32(disp);
            }
            else
            {
                // movq [rsp+disp],xmmN (no REX: plain MOVQ is unaligned-safe)
                B(0x0F); B(0x7F);
                B((byte)(0x84 + (i << 3))); B(0x24); D32(disp);
            }
        }
        B(0x48); B(0xB9); U64((ulong)siteHandle);       // mov rcx,site
        B(0x48); B(0x8D); B(0x94); B(0x24); D32(32);     // lea rdx,[rsp+32]
        B(0x48); B(0xB8); U64((ulong)prePtr);            // mov rax,pre
        B(0xFF); B(0xD0);                                // call rax
        B(0x85); B(0xC0);                                // test eax,eax
        int jnzOff = o;
        B(0x0F); B(0x85); D32(0);                        // jnz SKIP (patched below)
        for (int i = 0; i < n; i++)
        {
            int disp = 32 + 8 * i;
            if (shape.ParamKinds[i] == SlotKind.Int)
            {
                B((byte)(i < 2 ? 0x48 : 0x4C)); B(0x8B);
                B((byte)(i switch { 0 => 0x8C, 1 => 0x94, 2 => 0x84, _ => 0x8C }));
                B(0x24); D32(disp);
            }
            else
            {
                B(0x0F); B(0x6F);
                B((byte)(0x84 + (i << 3))); B(0x24); D32(disp);
            }
        }
        B(0x48); B(0x83); B(0xC4); B((byte)frame); // add rsp,FRAME
        B(0x48); B(0xB8); U64((ulong)trampoline);  // mov rax,trampoline
        B(0xFF); B(0xE0);                          // jmp rax
        int skipOff = o;
        B(0x48); B(0x83); B(0xC4); B((byte)frame); // add rsp,FRAME
        if (shape.Return == ReturnKind.Int)
        {
            B(0x31); B(0xC0); // xor eax,eax
        }
        else if (shape.Return == ReturnKind.Float)
        {
            B(0x0F); B(0x57); B(0xC0); // xorps xmm0,xmm0
        }
        B(0xC3); // ret
        int dispToSkip = skipOff - (jnzOff + 6);
        code[jnzOff + 2] = (byte)dispToSkip;
        code[jnzOff + 3] = (byte)(dispToSkip >> 8);
        code[jnzOff + 4] = (byte)(dispToSkip >> 16);
        code[jnzOff + 5] = (byte)(dispToSkip >> 24);

        var mem = nearTarget != IntPtr.Zero
            ? Nami.Wave.Internal.RawMemory.TryAllocExecutableNear((void*)nearTarget, (nuint)o)
            : Nami.Wave.Internal.RawMemory.AllocExecutable((nuint)o);
        if (mem == null)
        {
            throw new Wave.HookException(
                $"cannot allocate fast stub within reach of {(nint)nearTarget:X} (near-jump site)");
        }
        fixed (byte* src = code)
        {
            Buffer.MemoryCopy(src, (void*)mem, o, o);
        }
        var stub = (byte*)mem;
        Nami.Wave.Internal.RawMemory.FlushCode(stub, (nuint)o);
        Nami.Wave.Internal.RawMemory.MakeExecutable(stub, (nuint)o);
        return ((IntPtr)stub, (nuint)o);
    }
}
