# Wave - Nami's patching engine (CoreCLR side)

Wave is Nami's runtime method-patching engine for Nami's own .NET runtime. It installs **x64 inline detours** on managed
methods and routes calls through **owner-scoped chains of prefix/postfix callbacks** -
the "patching" layer of the Nami stack, built in-house (no Harmony, no MonoMod, no Cecil).
`Wave.Patch` picks the cheapest strategy that satisfies what was requested - a native
fast stub for prefix-only hooks on small GC-tracking-free signatures, the IL-copy body
otherwise - transparently upgrading/downgrading the site as owners come and go.
It does not touch game Mono: native Mono-export detours live in the `nami::tide` detour
toolkit, and the legacy inex lane brings its own unmodified BepInEx/Harmony stack.

```
src/Nami.Wave/           the engine
  Wave.cs                unified chain/sites: Wave.Hook (legacy) / FastPre dispatch /
                         A-stub (BuildDispatcher) / strategy rebuild + teardown
  Wave.Fast.cs           fast path: eligibility (IsGcTrackingFree), per-hook invokers,
                         B/D stub emitter, WavePatchEngine
  Wave.Patch.cs          M2 public API: Wave.Patch / Unpatch / UnpatchAll / IsPatched /
                         UnpatchEverything / GetPatchEngine + ILCopy strategy build
  Wave.Transpile.cs      transpilers (see below)
  Wave.Il2Cpp.cs         IL2CPP method patching: WaveIl2Cpp.Hook / HookFull / UnhookAll /
                         Il2CppHook - native dispatch-stub detours on GameAssembly methods
                         (fast observe/skip path; full path adds all-args + result
                         observation/rewriting; see tide.md §9)
  Internal/Detour.cs     one inline detour: prologue decode → trampoline → patch → restore
  Internal/X64Decoder.cs conservative x64 instruction-length decoder (relocation-safe)
  Internal/RawMemory.cs  W^X virtual-memory helpers (VirtualAlloc/VirtualProtect)
  Internal/NativeInterop.cs  method-code resolution incl. tiered-JIT jump-stub following
  Internal/IlReader.cs   raw IL decoder (opcodes, operands, branch targets)
  Internal/IlRewriter.cs re-emitter: original IL (incl. EH tables) → generated assembly method (IL copy)
  Internal/PatchedBodyBuilder.cs  prefix/postfix convention binder + ret-rewriting injector
  Wave.Transpile.cs        M2 transpilers: cursor IL rewriting (WaveIlCursor) + provenance conflicts
  Internal/IlNodes.cs      transpiler pipeline: body → label-anchored node list → emit
  Internal/CalliSignature.cs  ECMA-335 calli StandaloneSig parser - managed/unmanaged fnptr
                         call sites re-emitted faithfully; vararg/nested-fnptr/generic sites
                         refused with a precise error
tests/Nami.Wave.Tests/   104 tests in Release: M1 + M2
                         semantics, IL-copy fidelity, restore, the scope suite
                         (WavePatchScopeTests: closed generics, struct receivers, filter EH
                         clauses, tiny-method near detours, managed + unmanaged calli), and
                         the WaveIl2Cpp contract (no-loader behavior, arg validation,
                         HookFull). The deep M2 suite (WavePatchDeepTests) compiles in
                         Release only; in DEBUG only a placeholder runs instead - 57 [Fact]
                         + 2 [Theory] (6 rows) there (63 tests)
bench/Wave.Bench/        hooked-call overhead benchmark (1M calls)
```

## What it does

### M1 - native-stub dispatch (parameterless void targets, legacy entry point)

`Wave.Hook` is frozen: parameterless `void` targets, LIFO chain order, gate + observer.
It shares the unified chain (so Hook and Patch entries on one method compose), but new
code should use `Wave.Patch`.

```csharp
using Nami.Wave;

// Route Target.Ping() through an observer (original still runs):
Wave.Hook(method, owner: "my.mod.id", observer: () => Log("pinged"));

// Gate: return true to SKIP the original entirely:
Wave.Hook(method, owner: "my.mod.id", gate: () => !enabled);

// Multiple owners chain on one method; newest runs first (LIFO).
// Any gate returning true skips the original for the whole chain.
Wave.Unhook(method, "my.mod.id");     // one target
Wave.UnhookAll("my.mod.id");          // every target you hooked
```

Semantics (documented contract):

| Piece | Behavior |
|---|---|
| `gate` | Runs before the original. Returns `true` → the original is skipped. All gates run; any `true` wins. |
| `observer` | Runs before the original (or before a skip), right after its own owner's gate. Never prevents anything. |
| order | LIFO - the most recently hooked owner runs first; each owner's `gate` then `observer` run back-to-back. |
| owner | A string id (the mod id). One entry per owner per entry point; duplicates throw. |
| safety | A throwing callback is swallowed (best-effort); the game must not die because a mod callback threw. |

### M2 - Harmony-style IL-copy patching (closed methods)

```csharp
// Patch Player.TakeDamage(int amount) -> int: watch the args, rewrite the result.
Wave.Patch(takeDamage, owner: "my.mod.id",
    prefix:  (Player __instance, int amount, out object __state) => { Log($"hit for {amount}"); },
    postfix: (ref object __state, ref int __result) => { __result = Math.Min(__result, 1); });
// Return false from a bool prefix to SKIP the original body:
Wave.Patch(method, "my.mod.id", prefix: () => !modEnabled);

Wave.Unpatch(takeDamage, "my.mod.id");
```

Conventions are resolved from the hook delegate's parameter names:

| Parameter | Meaning |
|---|---|
| `Player __instance` | the receiver (instance targets) |
| `int amount` | any target parameter matched by name - **by value only** (`ref` hook params are refused) |
| `ref int __result` / `int __result` | the return value; `ref` lets a postfix rewrite it (postfix only) |
| `out object __state` / `ref object __state` | per-call state threaded prefix → postfix; must be `object` by ref |
| `object[] __args` | all arguments (including `this`) as an array - allocates an `object[]` + boxes per call, so use it sparingly |
| prefix returns `bool` | `false` → the original body is skipped (postfixes still run) |
| prefix returns `void` | original always runs |
| postfix returns `void` | always runs - also when the original was skipped |

Multiple owners patch the same method; prefixes run oldest-first (in hook order), postfixes
unwind newest-first - the chain runs in, then unwinds out in reverse. `Wave.Unpatch(method,
owner)` removes one owner and atomically rebuilds the patched body for the rest; unpatching the
last owner restores the original bytes exactly.

### Unified routing - one chain, two strategies

One site per target holds every owner's entries (Hook and Patch alike, ordered by one
sort: Hook entries keep LIFO-effective priority, Patch entries keep registration order).
On every add/remove Wave recomputes the cheapest strategy that satisfies the union:

- **Fast** (native stub): every entry is prefix-only (no postfix/transpiler) with
  by-value, name-matched hooks, and the target shape is GC-tracking-free - static,
  ≤4 params, each param and the return a reference-free primitive/enum/pointer, no
  byref. Blueprints by register-bank usage: **A** (no register args - the legacy M1
  stub, void returns), **B** (all-int args, spill GP only), **D** (mixed, spill both
  banks; XMM-only shapes also land here - a dedicated C blueprint waits on data).
- **ILCopy**: everything else (postfix, transpiler, instance methods, references,
  `ref`/`__result`/`__state`/`__args`, >4 params).

The fast stub runs the sorted prefix chain in one managed dispatch, then tail-jumps to
the original (or returns the type default on skip). There is deliberately no post
phase on the fast path - regaining control after the original would mean calling the
trampoline, which is unsound on CoreCLR (see research notes). Postfixes route to
ILCopy, where the body runs inline. `Wave.GetPatchEngine` reports the live strategy
per method; the bench asserts per-shape routing in CI, so a change that silently
narrows fast eligibility fails a gate instead of demoting mods quietly.

Hot-path contracts (all enforced by construction or test):
- Everything the stub can reach must already be JIT-compiled - hooks and invokers are
  pre-JITted at registration. First-call compilation under a GC-info-less stub frame
  is fatal (FailFast), not merely slow.
- The stub-called subgraph must not allocate on first call, for the same reason.
- The stub frame leaves rsp % 16 == 8 at the dispatch `call` (the callee then sees 0);
  tier-0 callees emit aligned spills that fault otherwise.
- Only reference-free values ever cross into spill slots (no managed refs, no byrefs).

### M2 transpilers - cursor IL rewriting with provenance

```csharp
// Rewrite the body itself: turn TakeDamage's subtraction into an addition.
Wave.Transpile(takeDamage, owner: "my.mod.id", il =>
{
    Assert.True(il.Goto(OpCodes.Sub));
    il.Replace(OpCodes.Add);
});
Wave.Patch(method, "my.mod.id", prefix: ..., postfix: ..., transpiler: il => { ... });
```

The cursor is an insertion point between instructions (never a list index): `Goto` seeks,
`Emit` inserts before `Current`, `Remove`/`Replace` mutate it, `MarkLabel` anchors a
branch target. Branch operands are first-class `WaveIlLabel`s anchored to nodes, so
inserting or removing instructions elsewhere can never redirect a jump - the silent-corruption
mode of index-based transpilers. Transpilers run before prefix/postfix wrapping, in
registration order, each seeing the previous one's output.

Every edit is stamped with its owner. Editing IL another owner introduced (or already
edited) still applies deterministically - later transpilers win - but records a
`WaveTranspilerConflict` on the `Wave.TranspilerConflict` event (and the bounded
`Wave.RecentTranspilerConflicts` ring) instead of composing silently.

## How it works

### Detour core (shared by both engines)

1. **Resolve the real code address.** `MethodHandle.GetFunctionPointer()` can return a
   tiered-JIT precode/jump stub (`E9 rel32` / `FF 25 disp32`). Wave forces JIT
   (`RuntimeHelpers.PrepareMethod`) then follows a bounded chain of leading jumps to the
   actual method body.
2. **Decode the prologue.** A conservative x64 decoder measures instructions until it has
   ≥ 14 bytes (the size of `mov rax, imm64; jmp rax`) of *relocatable* code. Anything it
   cannot measure with certainty (VEX/EVEX, 3-byte escapes, relative branches in the
   window) makes the hook **refuse loudly** rather than corrupt the process.
3. **Build trampolines.** The relocated prologue is emitted into an executable buffer with
   RIP-relative displacements recomputed for the new location, followed by a jump back into
   the original body past the patch site (the "run original" path). If the prologue pushes
   registers or reserves stack, a second **skip trampoline** is emitted: relocated prologue +
   synthesized unwind (`add rsp, N` + pops in reverse) + `ret`, so a gate can skip a framed
   method cleanly.
4. **Patch.** The first bytes of the method are replaced with an absolute jump to the detour
   target (page flipped to EXECUTE_READWRITE - never plain READWRITE, the target may
   share its page with live JIT code including the install frame itself - written, flipped back).

### Fast dispatch

Calls route through a **per-site native stub** (blueprint A/B/D): it spills the registers
its shape needs, calls the managed `FastPre` entry (via a GCHandle - never a raw object
ref across native) with the site handle and the spill block, then either tail-jumps to
the trampoline (the original runs with its original arguments on the caller's stack -
exceptions unwind naturally) or returns the type default on skip. No allocations on the
hot path; it cannot run anything after the original (no postfix phase - see research
notes).

### M2 IL-copy patching

Instead of calling the trampoline "as a function" (unsafe on CoreCLR x64 - see the research
notes at the end), Wave **copies the target's IL**:

1. The target's IL, locals, and exception handlers are decoded (`IlReader`) and re-emitted
   (`IlRewriter`) into a public static method on a generated type in a fresh dynamic
   assembly - tokens resolved from the original module, `IgnoresAccessChecksTo` applied for
   the Wave and target assemblies (third-assembly privates referenced by the IL can still
   fail). The generated method is a real managed method
   with a real `MethodHandle`, so its native entry is a valid detour target.
2. **Transpilers rewrite the copy first** (when present): the decoded body becomes a
   label-anchored node list (`IlNodes.From` - branch targets become labels, EH
   boundaries ride on nodes), each transpiler edits it through a cursor in registration
   order with provenance conflicts collected, and the edited list is emitted (`IlNodes.Emit`)
   with injected `ret`s still routed through the postfix tail.
3. **Prefixes are injected at the entry**, and **every `ret` is rewritten** into a postfix
   tail that stores the return value, runs the postfix chain (which may rewrite it via
   `ref`), and returns. A prefix returning `false` jumps straight to the first postfix tail,
   skipping the original body but still running postfixes.
4. The detour is retargeted at the generated method. The original method's own instructions
   now run *inside the generated copy*, so there is no recursive re-entry and no trampoline
   call - every call on the patched path is an ordinary managed call (GC-safe, unwind-safe,
   exception-safe).
5. Adding/removing an owner rebuilds the generated body and re-installs the detour; unhook
   restores the original bytes exactly.

Because the original body is *inlined* into the patched copy, patching semantics match
HarmonyX: skip via prefix, result rewriting via postfix `ref __result`, instance access via
`__instance` - with no per-call allocations on the happy path (declaring `__args`, or
writing `__state`, allocates per call).

## Measured overhead (x64, Release, .NET 10)

Exemplar numbers from `bench/Wave.Bench` (1M calls, noinline barrier - varies by machine):

```
baseline (direct)        : ~21 ns/call
hooked observer (M1)     : ~66 ns/call   (+45 ns)
hooked gate-skip (M1)    : ~73 ns/call   (+52 ns)
restored after unhook    : ~21 ns/call   (exact restore)
fast prefix, small sigs  : ~30-44 ns/call (blueprints A/B/D; no IL copy, original keeps its JIT state)
wave M2 prefix+postfix   : ~14 ns/call   (same-shaped pair vs HarmonyX below)
harmonyX prefix+postfix  : ~24 ns/call   (2.16.1, the BepInEx-6 fork)
```

The M1 hooked path is: detour jump → stub → one managed dispatch → callback(s) → tail-jump
→ original. No allocations on the hot path. M2's patched body runs the original instructions
inline plus one managed delegate call per hook - no marshaling on the hot path. (The M2
*call* path itself allocates nothing; only declaring `__args`, or writing `__state`, allocates
per call.) Note the honest reading of the numbers: on microbenchmarks M2's inline calls beat
the stub round-trip - the fast path wins on what it *doesn't* do (no IL copy, no extra
assembly, the original keeps its tier-1 JIT code), not on per-call nanoseconds. GC/EH-safe through ~1M-call horizons; past multi-10M tight loops, unwinding
across the stub's unmanaged frame is the known edge.

## Scope & honest limitations

- **Fast targets**: prefix-only hooks on GC-tracking-free shapes (static, ≤4 primitive/
  enum/pointer params, primitive/void return, by-value name-matched hooks). Skips return
  the type default. Anything richer (postfix, transpiler, instance, references, byref,
  `__`-conventions, >4 params) takes the IL-copy body automatically.
- **M1 targets**: `Wave.Hook` stays parameterless-`void`-only (frozen legacy entry point).
- **Trampoline soundness**: executing a relocated prologue requires every RIP-relative
  operand to still reach its target - the detour verifies this (`TrampolinesSound`) and
  the fast path refuses otherwise (IL-copy never executes trampolines, so it is
  unaffected). Far trampolines are allocated near the target to preserve reach.
- **M2 targets**: any closed method with a real body - static, instance (including
  struct receivers, observed by value), closed generics, any return type
  (ref returns untested), methods with exception handlers (including `catch-when`
  filters), multiple returns, recursion, bodies with locals, and `calli` calls
  (managed and unmanaged function pointers; exotic vararg/nested-fnptr call sites
  are refused with a precise error) are all handled. Open generic *definitions*
  have no machine code and are refused with an actionable error naming the exact
  closing step (`Wave.Patch(definition, typeArguments, owner, ...)` closes a generic
  method definition for you). Hook parameters bound to the target's
  parameters are **by-value only** (`ref`/`out` bindings throw `NotSupportedException`);
  `__instance` is by value, `__result` and `__state` are the by-ref convention parameters
  (`__state` must be `object` by ref; postfixes must return `void`).
- **Platform**: Windows x64. The decoder/detour are x64-specific by design.
- **Code shape**: Wave targets optimized (Release) JIT output - the code games ship. Debug
  builds may emit prologues the conservative decoder refuses; it throws rather than corrupts.
- **Tiered JIT**: hook methods that are already hot/stable. If the JIT later replaces the
  method body (promotion *after* hooking), the hook can be bypassed - the classic inline-
  detour limitation on modern .NET. Warm the method before hooking.
- **Tiny methods**: bodies with fewer than 5 clean prologue bytes cannot be detoured
  (refused); 5–13 clean bytes use a 5-byte relative jump with a near (±2GB) trampoline,
  14+ use the absolute jump. Refusals throw `HookException`, never corrupt.
- **Generated assemblies**: each patched-body *build* gets its own dynamic assembly, kept
  alive for the process lifetime (old builds are retained on rebuild - a slow leak per
  patch/unpatch cycle; patch sites are rare and rebuilds cheap, so this stays negligible).

## Why in-house (vs HarmonyX)

- Zero third-party dependency: no Cecil IL-weaving at patch time, no MonoMod.
- Owner-scoped chain and exact byte restore are first-class (not bolted on).
- The detour + decoder + dispatch core is ~2.3k lines you can read; the IL-copy +
  transpiler layer is another ~2.7k with a conservative refusal policy instead of a dependency.
- HarmonyX's model is IL-copy patching with delegate-based prefixes/postfixes; Wave now
  implements the same model on its own detour core, with the same `__instance`/`__result`/
  `__state`/`__args` conventions.

## Research notes: why not "call the trampoline as a function"

M1's tail-jump cannot observe a return value. The first M2 attempt detoured to an emitted
managed dispatcher that *called the trampoline* through `Marshal.GetDelegateForFunctionPointer`.
On x64 CoreCLR that is unsafe for value returns: the interop stub uses the unmanaged
convention while relocated JIT prologues are managed-convention code that re-reads arguments
from the caller's home area - producing garbage for leaf methods and AVs for framed ones.
Native frame thunks fixed *some* static value-typed shapes but crashed on void returns,
GC-tracked references, and instance methods. The reliable route is exactly what shipped:
**never call the trampoline; copy the IL so the original runs inline inside a real managed
method**. `Marshal.GetDelegateForFunctionPointer` on trampolines is a dead end on CoreCLR
and should not be revisited.

Two corollaries learned while building the fast path on top of trampolines:
- A far trampoline's jump-back must preserve `rax`/`xmm0` (rip-relative indirect jump,
  not `mov rax, addr`) - the original reaches it with a value return live.
- A trampoline can only *observe before* or *skip*; it can never run something *after*
  the original and see the result - which is why the fast stub has prefixes but no
  postfix phase. Postfixes need the inline body.
