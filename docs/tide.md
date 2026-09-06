# Tide — the Nami ↔ game bridge

Tide connects mods running on Nami's hosted .NET (CoreCLR) to the game's own managed runtime
(Unity Mono). It is the layer that lets a mod actually *touch the game* — call its code, read
its state — rather than only running sandboxed logic in a parallel universe.

```
src/Nami.Tide/              managed API: Tide, GameClass, GameObject, TideValue
native/loader/tide_pump.cpp native main-thread executor: mono_runtime_invoke hook, queue,
                            drain, trampoline
native/loader/tide_ops.cpp  native ops: UnityLog
native/loader/tide_objects.cpp  typed game access: field/property/method/object ops
native/loader/tide_abi.h    shared value/handle ABI (TideValue, CallRequest)
```

---

## 1. The problem: two managed runtimes, one process

Nami deliberately runs mods on a modern .NET 10 (CoreCLR) that it hosts inside the game
process — never on the game's ancient embedded Mono. That gives mods a modern BCL, real
`AssemblyLoadContext`s and hot-reload, but it creates a hard question:

> How does code in Nami's CoreCLR call code in the game's Mono?

Mono's embedding API (`mono_*` exports) is the door, but walking through it from the wrong
thread is fatal. Three approaches were tried and each crashed deterministically before the
working design emerged:

| Approach | Crash | Why |
|---|---|---|
| CoreCLR thread calls Mono directly | **coreclr.dll**, fixed offset | CoreCLR's GC cannot scan a thread that has also touched Mono's Boehm heap (two-GC conflict) |
| Dedicated native thread (Mono-attached) calls Mono | **mono-2.0-bdwgc.dll**, fixed offset (`0x59872`) | Unity's Mono can't execute embedding calls from a foreign `CreateThread` thread — its Boehm GC registration for such threads isn't fully wired (and `GC_register_my_thread` isn't exported) |
| The real bug behind both | same mono offset | `mono_assembly_loaded` takes a **`MonoAssemblyName*`**, not a `const char*` — Mono read the ASCII bytes as a struct and crashed. Fixed with `mono_assembly_name_new` |

Two separate lessons:

1. **Never call Mono from a thread that isn't the game's main thread.** The game main thread
   is the one Mono fully owns (created by Mono, registered with its GC, driven by its
   PlayerLoop).
2. **Check the embedding signatures.** Mono's API is C, untyped at the ABI level — a wrong
   pointer type crashes deep inside Mono at an offset that looks nothing like your bug.

---

## 2. How Tide works

Every Mono call is executed **on the game's main thread**. The mechanism:

```mermaid
sequenceDiagram
    participant Mod as Nami mod (CoreCLR)
    participant Tide as Nami.Tide
    participant Loader as nami_loader.dll
    participant Game as Game main thread (Mono)

    Mod->>Tide: Tide.UnityLog("hi")
    Tide->>Loader: nami_tide_unity_log(msg)  [P/Invoke]
    Loader->>Loader: enqueue op + wait on event
    Note over Game: game calls mono_runtime_invoke(...)
    Game->>Loader: (hooked) drain_queue()
    Loader->>Loader: run op: resolve UnityEngine.CoreModule → Debug → Log
    Loader->>Game: mono_runtime_invoke(Debug.Log, msg)  [on main thread]
    Loader->>Tide: signal done
    Tide-->>Mod: true
```

1. **Hook.** On first Tide use, `nami_loader.dll` installs a safe native detour over
   `mono_runtime_invoke` — an export the game's main thread calls constantly. The detour is
   built properly: the prologue length is measured by a small x64 decoder (whole instructions
   only, refuses branches/RIP-relative forms), relocated into a trampoline, and the entry is
   patched with `mov rax, imm64; jmp rax` under `VirtualProtect`. No split instructions.
2. **Queue.** A CoreCLR call enqueues a plain-data request (opcode + fixed-size struct) and
   blocks on a per-request event.
3. **Drain.** The next time the game main thread calls `mono_runtime_invoke`, the detour
   drains the queue **inline on the main thread** — running the Mono embedding calls where
   Mono's GC is fully set up — then calls the real `mono_runtime_invoke`.
4. **Fast path.** When nothing is queued, the detour costs one atomic read. Verified: the
   game runs normally with the hook installed (see §4).

The hook is a single global detour; the queue is guarded by a critical section plus an atomic
"work pending" flag so the hot path never takes the lock.

---

## 3. Enabling Tide

Tide is **opt-in**. Add to `<game>/nami/nami.json`:

```json
{
  "enableMonoBridge": true
}
```

On boot, Nami's runtime attaches Tide and fires a `Debug.Log` through the bridge as a
self-test. You should see in `nami.log`:

```
[boot] Tide bridge OK: Unity Debug.Log executed on the game main thread
```

> **Why opt-in?** The bridge runs native code that patches a live game export. It is proven
> on Unity 2022.3 Mono (Windows x64); until more games/versions are verified, it stays behind
> an explicit flag so a bad interaction can never silently affect a game that didn't ask
> for it.

---

## 4. Verified evidence (in-game)

Unity 2022.3.27f1 (Mono), Project Hardline, launched via `nami_boot`:

```
[tide] resolved root=... name_new=... loaded=... class=... invoke=...
[tide] tide op: Debug.Log executed OK
[boot] Tide bridge OK: Unity Debug.Log executed on the game main thread
[chainloader] Loaded dev.nami.samples.hello 0.1.0 (HelloNami.dll)
[boot] chainloader activated: 1 plugin(s) loaded
[dev.nami.samples.hello] HelloNami update tick 120  (running on .NET 10.0.10)
[dev.nami.samples.hello] HelloNami update tick 240
... (ticks continuously)
game alive and stable (~364 MB) for 35s+ with the hook installed
```

`UnityEngine.Debug.Log("hello from Nami's .NET runtime via Tide")` executed on the game's
Mono main thread from Nami's .NET 10, with the game stable and the mod's update loop running
throughout. Native diagnostics land in `<game>/nami/native/nami-tide.log`.

---

## 5. API

All Tide public types live in the **`Nami`** namespace (`Tide`, `GameClass`, `GameObject`,
`TideValue`, `TideType`).

```csharp
using Nami;   // Tide, GameClass, GameObject, TideValue
```

| Member | Description |
|---|---|
| `bool Tide.IsAvailable` | True when `nami_loader.dll` is loaded (i.e. running in-game under Nami). False in plain unit tests / outside a game. |
| `bool Tide.UnityLog(string message)` | Calls `UnityEngine.Debug.Log(object)` on the game main thread. |
| `GameClass GameClass.Resolve(assembly, ns, name)` | Resolve a game class once by assembly (with or without `.dll`). |
| `T GetStatic<T>(field)` / `SetStatic<T>(field, value)` | Typed static **field or property** read/write (`GetStaticInt/Float/Bool/String...`, `SetStatic...`). Properties (e.g. `Time.timeScale`) are resolved automatically when no field matches. |
| `CallStatic(method, args...)` / `CallStaticValue(...)` | Call a static method with typed args; returns a `TideValue`. |
| `GameObject.NewObject()` | Create a new instance of the class (runs the parameterless ctor). |
| `GameObject` | Opaque handle to a live game object. `Call(...)` instance methods, `GetInt/GetFloat/GetString/...` and `SetInt/...` instance fields/properties, `Dispose()` frees the handle. |
| `TideValue` | A typed value: `TideValue.FromInt/FromLong/FromFloat/FromDouble/FromBool/FromString/FromHandle`. |

**Blocking semantics**: every call blocks until the game's main thread has executed it (safe:
the main thread is always pumping through `mono_runtime_invoke`). String **arguments** are
freed automatically after the call; string **returns** must be copied before the next call
(the getter frees them for you).

**Failure**: ops that cannot resolve the assembly/class/member throw `TideException`; methods
that throw a Mono exception return a failure code. Details are logged to `nami-tide.log`.

### Example: a mod that touches the game

```csharp
using Nami;
using Nami.Sdk;

[NamiPlugin]
[PluginInfo("dev.example.greeter", "Greeter", "0.1.0")]
public sealed class GreeterPlugin : NamiPlugin
{
    public override void OnLoad()
    {
        if (!Tide.IsAvailable) return;

        // Static property read/write (Unity property, auto-resolved):
        var time = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Time");
        float scale = time.GetStaticFloat("timeScale");
        time.SetStaticFloat("timeScale", 0.5f);   // slow motion!

        // Create a live GameObject and call an instance method:
        var goClass = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "GameObject");
        using var go = goClass.NewObject();
        int id = go.CallIntMethod("GetInstanceID");
        Tide.UnityLog($"made GameObject #{id}");
    }
}
```

To reference Tide from a mod project, add a reference to `Nami.Tide.dll` (built from
`src/Nami.Tide`) the same way you reference `Nami.Sdk.dll`, and place the DLL next to
`Nami.Sdk.dll` in the game's `nami/` folder. Tide is a shared framework assembly: the
chainloader resolves `Nami.Tide` from the already-loaded copy, so your mod and the runtime
share one instance.

---

## 6. Internals (for contributors)

### Native: `tide_pump.cpp`

- `install_main_thread_drain()` — resolves `mono_runtime_invoke`, measures its prologue with
  `measure_relocatable_prologue()` (whole instructions ≥ 14 bytes; refuses relative branches
  and RIP-relative operands), builds a trampoline (`build_trampoline`), then writes the
  14-byte absolute jump under `VirtualProtect(PAGE_EXECUTE_READWRITE)`.
- `drain_queue()` — runs queued ops on the calling thread. Called from the detour, i.e. on
  the game main thread. Each request carries its own `done` event; the drain signals it after
  the op returns and frees the request.
- `run_on_main_thread(fn, arg, timeout)` — enqueues (critical section + atomic pending flag)
  and waits on the request's event.
- Fast path in the detour: one `InterlockedCompareExchange` on the pending flag; the lock is
  only taken when work is queued.

### Native: `tide_ops.cpp`

- `resolve_api()` — resolves the needed `mono_*` exports via `GetProcAddress` once.
- `find_assembly(name)` — builds a `MonoAssemblyName` via `mono_assembly_name_new` (tries
  with and without `.dll`) and calls `mono_assembly_loaded`. **Do not pass a raw string to
  `mono_assembly_loaded`** — see §1.
- Ops: `UnityLog`; diagnostics are appended to `nami-tide.log` next to the loader.

### Native: `tide_objects.cpp` (typed game access)

- `CallRequest` / `TideValue` (see `tide_abi.h`): one op entry (`nami_tide_object_op`) with
  sub-ops for field/property get/set, static/instance invoke, new-object and handle free.
- **Object handles**: objects cross the boundary as opaque 64-bit handles backed by Mono
  GCHandles (`mono_gchandle_new` pinned). CoreCLR never holds raw MonoObject pointers; the
  game GC keeps handled objects alive.
- **Values**: typed 16-byte slots (`TideValue`) — i32/i64/r4/r8/bool/string/object. Strings
  travel as caller-owned UTF-8 buffers read on the main thread; string returns are
  `mono_string_to_utf8` buffers the managed side frees via `nami_tide_free`.
- **Hierarchy-aware member lookup**: `mono_class_get_method_from_name`/`field` only search
  the class itself, so inherited members (e.g. `GameObject.GetInstanceID` on
  `UnityEngine.Object`) were missed; Tide walks `mono_class_get_parent` up the chain.
- **Property fallback**: when a "field" name is not a field, Tide resolves it as a property
  and uses its get/set method.

### Managed (`Nami.Tide` assembly, `Nami` namespace)

- `Tide` — availability + `UnityLog`.
- `GameClass` — resolve a class, typed static field/property access, static calls,
  `NewObject`.
- `GameObject` — opaque handle; typed instance field/property access, instance method calls,
  `Dispose` (frees the handle).
- `TideValue` / `TideType` — the typed marshaling values.
- All P/Invokes marshal a `CallRequest` (fixed name buffers + pointers to pinned `TideValue`
  arrays). No Mono knowledge lives in managed code — all of it is in the native ops.

---

## 7. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `Tide unavailable: nami_loader not loaded` | Not running in a Nami-injected game process (e.g. unit test, or game launched without `nami_boot`). |
| `Tide bridge present but UnityLog failed` | See `nami-tide.log`. Common: assembly not found under that name (Tide tries common variants) or a Mono exception in `Debug.Log`. |
| Game crashes on boot with the bridge on | The `mono_runtime_invoke` detour refused the prologue, or the game's Mono differs from 2022.3. Turn the bridge off (`"enableMonoBridge": false`), confirm the game runs, and report the `nami-tide.log`. |
| `TideException: op ... failed (code -1)` | Member not found (check assembly/class/member names, case, arity) or an unsupported value type. Codes are logged in `nami-tide.log`. |
| Reading a Unity property (e.g. `Time.timeScale`) crashes UnityPlayer | Unity *internal-call* property getters can crash when invoked from a nested `runtime_invoke` (inside the drain hook). Use fields or plain managed methods where possible; this is a known edge (see §8). |

---

## 8. Scope & roadmap

**Current (verified in-game, Unity 2022.3.27f1 Mono / Project Hardline):**
- Windows x64; opt-in via `enableMonoBridge`.
- Typed static **field/property** read/write (int/long/float/double/bool/string).
- Typed static method calls with primitive and string arguments.
- **Object creation** (`new GameObject()`) and **instance method calls with typed returns**
  (`GetInstanceID()`), via GC-handle-backed object handles.
- `UnityLog`.

In-game evidence (`nami.log`):
```
typed Debug.Log(string) call OK
typed Debug.Log(int) call OK (primitive arg marshaled)
created GameObject instance (handle=7688)
GameObject.GetInstanceID() = 0
TideProbe verification complete
game alive and stable (~379 MB)
```

**Known edge:** Unity internal-call property getters invoked from the drain's nested
`runtime_invoke` can crash UnityPlayer (observed with `Time.timeScale`). Plain managed
fields/methods and the instance path work. Fix direction: drain from a non-`runtime_invoke`
main-thread hook point.

**Next:**
- Fix the internal-call property edge (drain outside nested `runtime_invoke`).
- `MonoBehaviour`-style instance access on live scene objects (find by type/name).
- Enum and array/collection values.
- A typed projection layer (generated strongly-typed wrappers over `GameClass`) so mods get
  near-native ergonomics instead of stringly-typed calls.
- Broaden the verified matrix (older/newer Unity Mono, more games); the main-thread-drain
  pattern is expected to carry over.

The same main-thread-drain architecture will inform the IL2CPP bridge later: the lesson —
"run game-runtime calls where the game's runtime owns the thread" — is universal.
