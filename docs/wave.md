# Wave — Nami's patching engine

Wave is Nami's runtime method-patching engine. It installs **x64 inline detours** on managed
methods and routes calls through an **owner-scoped chain of callbacks** — the "patching"
layer of the Nami stack, built in-house (no Harmony, no MonoMod, no Cecil).

```
src/Nami.Wave/           the engine
  Wave.cs                public API: Wave.Hook / Unhook / UnhookAll / IsHooked
  Internal/Detour.cs     one inline detour: prologue decode → trampoline → patch → restore
  Internal/X64Decoder.cs conservative x64 instruction-length decoder (relocation-safe)
  Internal/RawMemory.cs  W^X virtual-memory helpers (VirtualAlloc/VirtualProtect)
  Internal/NativeInterop.cs  method-code resolution incl. tiered-JIT jump-stub following
tests/Nami.Wave.Tests/   15 tests: redirect, skip, chain, owners, re-hook, overhead
bench/Wave.Bench/        hooked-call overhead benchmark
```

## What it does

```csharp
using Nami.Wave;

// Route Target.Ping() through an observer (original still runs):
Wave.Hook(method, owner: "my.mod.id", observer: () => Log("pinged"));

// Gate: return true to SKIP the original entirely:
Wave.Hook(method, owner: "my.mod.id", gate: () => !enabled);

// Multiple owners chain on one method; newest runs first (LIFO).
// Any gate returning true skips the original for the whole chain.

// Unhook exactly your own patches:
Wave.Unhook(method, "my.mod.id");     // one target
Wave.UnhookAll("my.mod.id");          // every target you hooked
```

Semantics (documented contract):

| Piece | Behavior |
|---|---|
| `gate` | Runs before the original. Returns `true` → the original is skipped. All gates run; any `true` wins. |
| `observer` | Runs after the original (or after a skip). Never prevents anything. |
| order | LIFO — the most recently hooked owner runs first. |
| owner | A string id (the mod id). One owner per target; duplicate throws. |
| safety | A throwing callback is swallowed (best-effort); the game must not die because a mod callback threw. |

## How it works

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
4. **Patch.** The first 14 bytes of the method are replaced with an absolute jump to a
   **per-site native dispatcher stub** (W^X: page flipped writable, written, flipped back).
   The stub preserves the original argument registers, calls the managed `DispatchSite`
   (via a GCHandle — never a raw object ref across native), and then either tail-jumps to
   the trampoline (original runs with its original arguments on the caller's stack) or to
   the skip trampoline / returns.
5. **Unhook.** The original bytes are restored exactly. Detour + stub + trampoline memory is
   freed. Verified: a restored method benchmarks at its original speed and behaves
   identically.

## Measured overhead (x64, Release, .NET 10)

From `bench/Wave.Bench` (1M calls, noinline barrier):

```
baseline (direct)        : ~21 ns/call
hooked observer          : ~66 ns/call   (+45 ns)
hooked gate-skip         : ~73 ns/call   (+52 ns)
restored after unhook    : ~21 ns/call   (exact restore)
```

The hooked path is: detour jump → stub → one managed dispatch → callback(s) → tail-jump →
original. No allocations on the hot path.

## Scope & honest limitations (M1)

- **Targets**: parameterless `void` methods only. Value-returning or parameterized targets
  throw `HookException` (per-signature dispatch via IL emission is the next milestone).
- **Platform**: Windows x64. The decoder/detour are x64-specific by design.
- **Code shape**: Wave targets optimized (Release) JIT output — the code games ship. Debug
  builds may emit prologues the conservative decoder refuses; it throws rather than corrupts.
- **Tiered JIT**: hook methods that are already hot/stable. If the JIT later replaces the
  method body (promotion *after* hooking), the hook can be bypassed — the classic inline-
  detour limitation on modern .NET. Warm the method before hooking.
- **Tiny methods**: a body smaller than the 14-byte jump cannot be detoured inline (refused).
- **Stress envelope**: verified stable through ~1M hooked calls and in all functional tests.
  A documented edge exists in multi-10M tight loops where exception unwinding crosses the
  dispatcher stub's unmanaged frame (GC/EH failfast). Real game hook rates are far below
  this; the fix (a runtime-known managed→native thunk with unwind info) is tracked as a
  research item.

## Why in-house (vs HarmonyX)

- Zero third-party dependency: no Cecil IL-weaving at patch time, no MonoMod.
- Owner-scoped chain and exact byte restore are first-class (not bolted on).
- The detour, decode and dispatch are ~600 lines you can read.
- The IL-emission layer (arbitrary signatures, `__result`/`__instance`-style access) can be
  added on top of this core without changing its safety model.
