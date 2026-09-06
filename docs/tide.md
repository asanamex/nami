# Tide — the Nami ↔ game bridge

Tide connects mods running on Nami's hosted .NET (CoreCLR) to the game's own managed runtime
(Unity Mono). It is the layer that lets a mod actually *touch the game* — call its code, read
its state — rather than only running sandboxed logic.

```
src/Nami.Tide/            managed bridge API (what mods call)
native/loader/tide_pump.cpp   native main-thread executor (mono_runtime_invoke hook + queue)
native/loader/tide_ops.cpp    native ops: UnityLog, InvokeStatic (resolve + call game code)
```

## Why this architecture (the hard-won part)

Two managed runtimes in one process do not mix naively. Getting Tide to work meant
disproving three plausible approaches with real crashes:

1. **Direct calls from a CoreCLR thread into Mono** → crashes *inside CoreCLR's GC* at a fixed
   offset. CoreCLR's GC cannot scan a thread that has touched Mono's Boehm heap.
2. **Calls from a dedicated native pump thread** (attached to Mono, never registered with
   CoreCLR) → crashes *inside Mono's Boehm GC* at a fixed offset on the first allocating call.
   Unity's Mono cannot safely execute embedding calls from a foreign `CreateThread` thread.
3. **The actual bug hiding behind both**: `mono_assembly_loaded` takes a `MonoAssemblyName*`,
   not a `const char*`. Passing a C string made Mono read garbage as a struct — a
   deterministic crash at the same offset regardless of thread. Fixing the signature is what
   made the crash disappear.

The architecture that works:

- **Every Mono call executes on the game's MAIN thread** — the one thread Mono fully owns.
- `nami_loader.dll` hooks `mono_runtime_invoke` (which the game main thread calls constantly)
  with a safe native detour (decoder-measured prologue + trampoline, no split instructions).
- The detour drains a lock-free-ish queue of native ops **inline on the main thread**, then
  calls the real `mono_runtime_invoke`.
- CoreCLR (Tide managed) enqueues an op and blocks on an event until the main thread ran it.
- The fast path (nothing queued) is a single atomic read — negligible overhead on the game's
  hottest path (verified: game runs normally, mod ticks continuously).

## Verified in-game

Unity 2022.3.27f1 (Mono), Project Hardline, via `nami_boot`:

```
[tide]  resolved root=... name_new=... loaded=... class=... invoke=...
[tide]  tide op: Debug.Log executed OK
[boot]  Tide bridge OK: Unity Debug.Log executed on the game main thread
[chainloader] Loaded dev.nami.samples.hello ...
[dev.nami.samples.hello] HelloNami update tick 120 ... (ticks continuously)
game alive and stable (364 MB, 35s+)
```

`UnityEngine.Debug.Log("...")` executed on the game's Mono main thread, from Nami's .NET 10
runtime, with the game stable. The config flag that enables it: `"enableMonoBridge": true`
in `nami.json`.

## API (M1)

```csharp
using Nami.Tide;

if (Tide.IsAvailable)
{
    Tide.UnityLog("hello from a Nami mod");                          // Debug.Log on the game

    // Call a parameterless static method on any game class:
    Tide.InvokeStatic("Assembly-CSharp", "MyGame", "MyClass", "MyMethod");
}
```

Both calls block until the game main thread has executed them (safe: the main thread is
always pumping through `mono_runtime_invoke`). Returns `false` if the op failed (missing
assembly/class/method or a Mono exception).

## Scope & next steps

- **Ops today**: `UnityLog` (string → `Debug.Log`) and `InvokeStatic` (parameterless static
  method by assembly/namespace/class/method name).
- **Next**: static field read/write, method calls with primitive/string args, instance access
  via object handles, then a typed projection layer so mods get near-native ergonomics.
- The `mono_runtime_invoke` hook is a single global detour; the queue and drain are
  concurrency-safe (critical section + atomic pending flag).
- Windows x64, Unity Mono 2022.3 era verified; the same main-thread-drain pattern will apply
  to other Mono Unity versions.
