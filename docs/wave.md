# Wave — Nami's patching engine

Wave is Nami's runtime method-patching engine. It installs **x64 inline detours** on managed
methods and routes calls through **owner-scoped chains of prefix/postfix callbacks** — the
"patching" layer of the Nami stack, built in-house (no Harmony, no MonoMod, no Cecil).

```
src/Nami.Wave/           the engine
  Wave.cs                M1 public API: Wave.Hook / Unhook / UnhookAll / IsHooked / UnhookEverything
  Wave.Patch.cs          M2 public API: Wave.Patch / Unpatch / UnpatchAll / IsPatched / UnpatchEverything
  Internal/Detour.cs     one inline detour: prologue decode → trampoline → patch → restore
  Internal/X64Decoder.cs conservative x64 instruction-length decoder (relocation-safe)
  Internal/RawMemory.cs  W^X virtual-memory helpers (VirtualAlloc/VirtualProtect)
  Internal/NativeInterop.cs  method-code resolution incl. tiered-JIT jump-stub following
  Internal/IlReader.cs   raw IL decoder (opcodes, operands, branch targets)
  Internal/IlRewriter.cs re-emitter: original IL (incl. EH tables) → generated assembly method (IL copy)
  Internal/PatchedBodyBuilder.cs  prefix/postfix convention binder + ret-rewriting injector
tests/Nami.Wave.Tests/   37 [Fact] + 1 [Theory] (2 rows) in Release: M1 + M2 semantics,
                         IL-copy fidelity, restore (the deep M2 suite compiles in Release;
                         in DEBUG only a placeholder runs)
bench/Wave.Bench/        hooked-call overhead benchmark (1M calls)
```

## What it does

### M1 — native-stub dispatch (parameterless void targets)

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
| order | LIFO — the most recently hooked owner runs first; each owner's `gate` then `observer` run back-to-back. |
| owner | A string id (the mod id). One owner per target; duplicate throws. |
| safety | A throwing callback is swallowed (best-effort); the game must not die because a mod callback threw. |

### M2 — Harmony-style IL-copy patching (closed methods)

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
| `int amount` | any target parameter matched by name — **by value only** (`ref` hook params are refused) |
| `ref int __result` / `int __result` | the return value; `ref` lets a postfix rewrite it (postfix only) |
| `out object __state` / `ref object __state` | per-call state threaded prefix → postfix; must be `object` by ref |
| `object[] __args` | all arguments (including `this`) as an array — allocates an `object[]` + boxes per call, so use it sparingly |
| prefix returns `bool` | `false` → the original body is skipped (postfixes still run) |
| prefix returns `void` | original always runs |
| postfix returns `void` | always runs — also when the original was skipped |

Multiple owners patch the same method; prefixes run oldest-first (in hook order), postfixes
unwind newest-first — the chain runs in, then unwinds out in reverse. `Wave.Unpatch(method,
owner)` removes one owner and atomically rebuilds the patched body for the rest; unpatching the
last owner restores the original bytes exactly.

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
   target (W^X: page flipped writable, written, flipped back).

### M1 dispatch

Calls route through a **per-site native dispatcher stub** that preserves the original
argument registers, calls the managed `DispatchSite` (via a GCHandle — never a raw object
ref across native), then either tail-jumps to the trampoline (the original runs with its
original arguments on the caller's stack) or to the skip trampoline / returns. This is the
zero-allocation hot path; it cannot observe return values (the original returns straight to
the game caller).

### M2 IL-copy patching

Instead of calling the trampoline "as a function" (unsafe on CoreCLR x64 — see the research
notes at the end), Wave **copies the target's IL**:

1. The target's IL, locals, and exception handlers are decoded (`IlReader`) and re-emitted
   (`IlRewriter`) into a public static method on a generated type in a fresh dynamic
   assembly — tokens resolved from the original module, `IgnoresAccessChecksTo` applied for
   the Wave and target assemblies (third-assembly privates referenced by the IL can still
   fail). The generated method is a real managed method
   with a real `MethodHandle`, so its native entry is a valid detour target.
2. **Prefixes are injected at the entry**, and **every `ret` is rewritten** into a postfix
   tail that stores the return value, runs the postfix chain (which may rewrite it via
   `ref`), and returns. A prefix returning `false` jumps straight to the first postfix tail,
   skipping the original body but still running postfixes.
3. The detour is retargeted at the generated method. The original method's own instructions
   now run *inside the generated copy*, so there is no recursive re-entry and no trampoline
   call — every call on the patched path is an ordinary managed call (GC-safe, unwind-safe,
   exception-safe).
4. Adding/removing an owner rebuilds the generated body and re-installs the detour; unhook
   restores the original bytes exactly.

Because the original body is *inlined* into the patched copy, patching semantics match
HarmonyX: skip via prefix, result rewriting via postfix `ref __result`, instance access via
`__instance` — with no per-call allocations on the happy path (declaring `__args`, or
writing `__state`, allocates per call).

## Measured overhead (x64, Release, .NET 10)

Exemplar numbers from `bench/Wave.Bench` (1M calls, noinline barrier — varies by machine):

```
baseline (direct)        : ~21 ns/call
hooked observer (M1)     : ~66 ns/call   (+45 ns)
hooked gate-skip (M1)    : ~73 ns/call   (+52 ns)
restored after unhook    : ~21 ns/call   (exact restore)
```

The M1 hooked path is: detour jump → stub → one managed dispatch → callback(s) → tail-jump
→ original. No allocations on the hot path. M2's patched body runs the original instructions
inline plus one managed delegate call per hook — no marshaling on the hot path. (The M2
*call* path itself allocates nothing; only declaring `__args`, or writing `__state`, allocates
per call.) GC/EH-safe through ~1M-call horizons; past multi-10M tight loops, unwinding
across the stub's unmanaged frame is the known edge.

## Scope & honest limitations

- **M1 targets**: parameterless `void` methods (kept for its zero-allocation hot path and
  native skip semantics).
- **M2 targets**: any closed method with a real body (open generics are refused) — static or
  instance, any return type (ref returns untested), methods with exception handlers, multiple
  returns, and recursion are all handled. Struct instance methods, `calli` bodies and
  filter-style exception clauses are refused loudly. Hook parameters bound to the target's
  parameters are **by-value only** (`ref`/`out` bindings throw `NotSupportedException`);
  `__instance` is by value, `__result` and `__state` are the by-ref convention parameters
  (`__state` must be `object` by ref; postfixes must return `void`).
- **Platform**: Windows x64. The decoder/detour are x64-specific by design.
- **Code shape**: Wave targets optimized (Release) JIT output — the code games ship. Debug
  builds may emit prologues the conservative decoder refuses; it throws rather than corrupts.
- **Tiered JIT**: hook methods that are already hot/stable. If the JIT later replaces the
  method body (promotion *after* hooking), the hook can be bypassed — the classic inline-
  detour limitation on modern .NET. Warm the method before hooking.
- **Tiny methods**: a body smaller than the 14-byte jump cannot be detoured inline (refused).
- **Generated assemblies**: each patched-body *build* gets its own dynamic assembly, kept
  alive for the process lifetime (old builds are retained on rebuild — a slow leak per
  patch/unpatch cycle; patch sites are rare and rebuilds cheap, so this stays negligible).

## Why in-house (vs HarmonyX)

- Zero third-party dependency: no Cecil IL-weaving at patch time, no MonoMod.
- Owner-scoped chain and exact byte restore are first-class (not bolted on).
- The detour + decoder + dispatch core is ~1.4k lines you can read; the IL-copy layer is
  another ~1.3k with a conservative refusal policy instead of a dependency.
- HarmonyX's model is IL-copy patching with delegate-based prefixes/postfixes; Wave now
  implements the same model on its own detour core, with the same `__instance`/`__result`/
  `__state`/`__args` conventions.

## Research notes: why not "call the trampoline as a function"

M1's tail-jump cannot observe a return value. The first M2 attempt detoured to an emitted
managed dispatcher that *called the trampoline* through `Marshal.GetDelegateForFunctionPointer`.
On x64 CoreCLR that is unsafe for value returns: the interop stub uses the unmanaged
convention while relocated JIT prologues are managed-convention code that re-reads arguments
from the caller's home area — producing garbage for leaf methods and AVs for framed ones.
Native frame thunks fixed *some* static value-typed shapes but crashed on void returns,
GC-tracked references, and instance methods. The reliable route is exactly what shipped:
**never call the trampoline; copy the IL so the original runs inline inside a real managed
method**. `Marshal.GetDelegateForFunctionPointer` on trampolines is a dead end on CoreCLR
and should not be revisited.
