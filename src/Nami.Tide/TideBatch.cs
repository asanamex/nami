using System.Runtime.InteropServices;
using System.Text;

namespace Nami;

/// <summary>
/// Batched Tide operations: enqueue N game-side ops, then <see cref="Flush"/> them in ONE
/// main-thread round trip. A mod that reads five fields per tick pays one thread hop
/// instead of five — on bridges like this the hop, not the marshaling, is the dominant
/// per-call cost.
///
/// Usage pattern (mirrors the sync GameClass/GameObject API, deferred):
/// <code>
/// using var batch = new TideBatch();
/// int a = batch.EnqueueCallStatic(mathClass, "Max", TideValue.FromInt(3), TideValue.FromInt(7), TideType.I32);
/// int b = batch.EnqueueGetStatic(playerClass, "score", TideType.I32);
/// batch.EnqueueSetStatic(playerClass, "score", TideValue.FromInt(100)); // returns an index too
/// batch.Flush();                                   // ONE blocking round trip
/// int max = batch.GetInt(a);                       // read results afterwards
/// batch.GetString(i)  // string results MUST be read before Dispose (frees them)
/// </code>
///
/// Argument <see cref="TideValue"/>s passed to Enqueue* are consumed (their string buffers
/// are freed by the batch after Flush), mirroring the sync API's argument discipline.
/// An op that fails does not stop the batch: every op executes, and per-op outcomes are
/// surfaced via <see cref="WasOk"/>/<see cref="CodeOf"/>; result accessors throw
/// <see cref="Tide.TideException"/> for a failed op.
/// </summary>
public sealed unsafe class TideBatch : IDisposable
{
    private const int MaxOps = 256;

    private struct Entry
    {
        public CallRequest* Req;        // unmanaged request block
        public TideValue* Args;         // unmanaged args array (may be null)
        public TideValue* Ret;          // unmanaged ret slot (null for void ops)
        public TideCallOp Op;
        public string Member;           // for error messages
        public string Target;           // class or "TypeName.instance" for error messages
    }

    private readonly List<Entry> _entries = new();
    private readonly int[] _codes = new int[MaxOps];   // filled by Flush
    private bool _flushed;
    private bool _disposed;

    /// <summary>Number of ops enqueued so far.</summary>
    public int Count => _entries.Count;

    // ------------------------------------------------------------------ enqueue

    private int Enqueue(TideCallOp op, string assembly, string ns, string klass, string member,
        TideValue[]? args, TideType returnType, string targetLabel)
    {
        ThrowIfDisposed();
        if (_flushed)
        {
            throw new InvalidOperationException("this TideBatch has already been flushed");
        }
        if (_entries.Count >= MaxOps)
        {
            throw new InvalidOperationException($"TideBatch supports at most {MaxOps} ops");
        }

        var req = (CallRequest*)Marshal.AllocHGlobal(sizeof(CallRequest));
        *req = default;
        Fill(req->Assembly, 160, assembly ?? string.Empty);
        Fill(req->Ns, 160, ns ?? string.Empty);
        Fill(req->Klass, 160, klass ?? string.Empty);
        Fill(req->Member, 160, member ?? string.Empty);
        req->Op = op;
        req->HandleCapacity = 64;

        TideValue* argsPtr = null;
        if (args is { Length: > 0 })
        {
            argsPtr = (TideValue*)Marshal.AllocHGlobal(sizeof(TideValue) * args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                argsPtr[i] = args[i];
            }
        }
        req->Args = argsPtr;
        req->ArgCount = args?.Length ?? 0;

        TideValue* retPtr = null;
        if (returnType != TideType.Void)
        {
            retPtr = (TideValue*)Marshal.AllocHGlobal(sizeof(TideValue));
            *retPtr = default;
            retPtr->Type = returnType;  // REQUESTED type; the op marshals against it
        }
        req->Ret = retPtr;

        _entries.Add(new Entry
        {
            Req = req,
            Args = argsPtr,
            Ret = retPtr,
            Op = op,
            Member = member ?? string.Empty,
            Target = targetLabel,
        });
        return _entries.Count - 1;
    }

    /// <summary>Enqueues a static field read; returns the result index for Flush + Get*.</summary>
    public int EnqueueGetStatic(GameClass target, string field, TideType resultType)
        => Enqueue(TideCallOp.GetStaticField, target.Assembly, target.Namespace, target.Name,
            field, null, resultType, target.Name);

    /// <summary>Enqueues a static field write.</summary>
    public int EnqueueSetStatic(GameClass target, string field, TideValue value)
        => Enqueue(TideCallOp.SetStaticField, target.Assembly, target.Namespace, target.Name,
            field, new[] { value }, TideType.Void, target.Name);

    /// <summary>Enqueues a static method call; <paramref name="resultType"/> Void = fire-and-forget.</summary>
    public int EnqueueCallStatic(GameClass target, string method, TideType resultType, params TideValue[] args)
        => Enqueue(TideCallOp.InvokeStatic, target.Assembly, target.Namespace, target.Name,
            method, args, resultType, target.Name);

    /// <summary>Enqueues an instance field read on a live game object.</summary>
    public int EnqueueGetInstance(GameObject obj, string field, TideType resultType)
    {
        ThrowIfDisposed(obj);
        return Enqueue(TideCallOp.GetInstanceField, string.Empty, string.Empty, string.Empty,
            field, new[] { TideValue.FromHandle(obj.HandleValue) }, resultType, $"instance {obj.HandleValue}");
    }

    /// <summary>Enqueues an instance field write on a live game object.</summary>
    public int EnqueueSetInstance(GameObject obj, string field, TideValue value)
    {
        ThrowIfDisposed(obj);
        return Enqueue(TideCallOp.SetInstanceField, string.Empty, string.Empty, string.Empty,
            field, new[] { TideValue.FromHandle(obj.HandleValue), value }, TideType.Void,
            $"instance {obj.HandleValue}");
    }

    /// <summary>Enqueues an instance method call (the `this` handle is prepended for you).</summary>
    public int EnqueueCallInstance(GameObject obj, string method, TideType resultType, params TideValue[] args)
    {
        ThrowIfDisposed(obj);
        var withThis = new TideValue[args.Length + 1];
        withThis[0] = TideValue.FromHandle(obj.HandleValue);
        Array.Copy(args, 0, withThis, 1, args.Length);
        return Enqueue(TideCallOp.InvokeInstance, string.Empty, string.Empty, string.Empty,
            method, withThis, resultType, $"instance {obj.HandleValue}");
    }

    // ------------------------------------------------------------------ flush

    /// <summary>
    /// Runs every enqueued op on the game main thread in ONE round trip and stores the
    /// results. Throws <see cref="InvalidOperationException"/> when the loader is absent,
    /// <see cref="Tide.TideException"/> when the batch itself could not run (e.g. the
    /// main-thread pump is unavailable). Per-op game-side failures do NOT throw here —
    /// check <see cref="WasOk"/> or let the result accessors throw.
    /// </summary>
    public void Flush()
    {
        ThrowIfDisposed();
        if (_flushed)
        {
            throw new InvalidOperationException("this TideBatch has already been flushed");
        }
        if (_entries.Count == 0)
        {
            _flushed = true;  // nothing to do; no loader round trip needed
            return;
        }
        if (!Tide.IsAvailable)
        {
            throw new InvalidOperationException(
                "Tide is not available (nami_loader.dll is not loaded; batch ops need a game process)");
        }

        var backend = Tide.ActiveBackend;
        int n = _entries.Count;

        var reqPtrs = (CallRequest**)Marshal.AllocHGlobal(sizeof(CallRequest*) * n);
        var codes = (int*)Marshal.AllocHGlobal(sizeof(int) * n);
        for (int i = 0; i < n; i++)
        {
            codes[i] = -3;  // defined even when the export bails before writing (rc == -1)
            reqPtrs[i] = _entries[i].Req;
        }

        try
        {
            var batch = new BatchRequest
            {
                Requests = reqPtrs,
                Codes = codes,
                Count = n,
            };

            var timed = Nami.Sdk.TideMetrics.HasSink;
            var sw = timed ? System.Diagnostics.Stopwatch.StartNew() : null;
            int rc = backend == Tide.Backend.Il2Cpp
                ? Tide.NativeIl2CppObjectOpBatch(&batch)
                : Tide.NativeObjectOpBatch(&batch);
            if (sw is not null)
            {
                sw.Stop();
                Nami.Sdk.TideMetrics.Record(sw.Elapsed.TotalMilliseconds);
            }

            for (int i = 0; i < n; i++)
            {
                _codes[i] = codes[i];
            }

            // Free the string ARG buffers we consumed (AllocHGlobal by TideValue.FromString),
            // mirroring the sync API's post-call discipline — on SUCCESS and FAILURE both
            // (the ops copy shallow, they never own these buffers). Ret slots stay alive
            // for reads.
            foreach (var e in _entries)
            {
                if (e.Args == null)
                {
                    continue;
                }
                for (int i = 0; i < e.Req->ArgCount; i++)
                {
                    if (e.Args[i].Type == TideType.String && e.Args[i].Data.Str.Utf8 != null)
                    {
                        Marshal.FreeHGlobal((IntPtr)e.Args[i].Data.Str.Utf8);
                        e.Args[i].Data.Str.Utf8 = null;
                    }
                }
            }

            if (rc != 0)
            {
                // Batch-level failure: the pump never ran the ops (or bad shape). Per-op
                // codes were set by the export when it could (-3 on pump-unavailable).
                throw new Tide.TideException(
                    $"Tide batch of {n} ops failed (code {rc})") { Code = rc };
            }
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)reqPtrs);
            Marshal.FreeHGlobal((IntPtr)codes);
        }

        _flushed = true;
    }

    // ------------------------------------------------------------------ results

    private void EnsureResult(int index, TideType expected)
    {
        ThrowIfDisposed();
        if (index < 0 || index >= _entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        if (!_flushed)
        {
            throw new InvalidOperationException("call Flush() before reading batch results");
        }
        var e = _entries[index];
        if (_codes[index] != 0)
        {
            throw Error(index, e, _codes[index]);
        }
        if (e.Ret == null || e.Ret->Type != expected)
        {
            throw new InvalidOperationException(
                $"op {index} does not produce a {expected} result (enqueued as {_entries[index].Op})");
        }
    }

    /// <summary>True when op <paramref name="index"/> completed successfully.</summary>
    public bool WasOk(int index) => index >= 0 && index < _entries.Count && _flushed && _codes[index] == 0;

    /// <summary>The native TideResult code for op <paramref name="index"/> (0 = ok). Requires Flush.</summary>
    public int CodeOf(int index)
    {
        ThrowIfDisposed();
        if (!_flushed)
        {
            throw new InvalidOperationException("call Flush() before reading batch results");
        }
        return index >= 0 && index < _entries.Count
            ? _codes[index]
            : throw new ArgumentOutOfRangeException(nameof(index));
    }

    public int GetInt(int index) { EnsureResult(index, TideType.I32); return _entries[index].Ret->Data.I32; }
    public long GetLong(int index) { EnsureResult(index, TideType.I64); return _entries[index].Ret->Data.I64; }
    public float GetFloat(int index) { EnsureResult(index, TideType.R4); return _entries[index].Ret->Data.R4; }
    public double GetDouble(int index) { EnsureResult(index, TideType.R8); return _entries[index].Ret->Data.R8; }
    public bool GetBool(int index) { EnsureResult(index, TideType.Bool); return _entries[index].Ret->Data.Bool != 0; }
    public long GetHandle(int index) { EnsureResult(index, TideType.Object); return _entries[index].Ret->Data.Handle; }

    /// <summary>Reads (and frees) a string result. Must be called before <see cref="Dispose"/>.</summary>
    public string? GetString(int index)
    {
        EnsureResult(index, TideType.String);
        var ret = _entries[index].Ret;
        try
        {
            return ret->Data.Str.Utf8 == null
                ? null
                : Marshal.PtrToStringUTF8((IntPtr)ret->Data.Str.Utf8, ret->Data.Str.Len);
        }
        finally
        {
            ret->FreeNativeReturn();
        }
    }

    /// <summary>Wraps an object result as a caller-owned handle (Dispose it when done).</summary>
    public GameObject? GetObject(int index)
        => GetHandle(index) == 0 ? null : GameObject.FromHandle(GetHandle(index));

    private Tide.TideException Error(int index, Entry e, int rc)
    {
        var detail = ErrorMessage(e.Req);
        var reason = rc == -2 ? "the game method threw an exception" : $"code {rc}";
        var msg = $"Tide batch op {index} ({e.Op} on {e.Target}.{e.Member}) failed ({reason})" +
                  (detail.Length > 0 ? $": {detail}" : string.Empty);
        return new Tide.TideException(msg) { Code = rc };
    }

    private static string ErrorMessage(CallRequest* req)
    {
        byte* p = req->ErrorMessage;
        int len = 0;
        while (len < 512 && p[len] != 0)
        {
            len++;
        }
        return len > 0 ? Encoding.UTF8.GetString(p, len) : string.Empty;
    }

    private static void Fill(byte* dst, int capacity, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        int n = Math.Min(bytes.Length, capacity - 1);
        for (int i = 0; i < n; i++)
        {
            dst[i] = bytes[i];
        }
        dst[n] = 0;
    }

    private static void ThrowIfDisposed(GameObject obj)
    {
        if (obj.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(GameObject));
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TideBatch));
        }
    }

    /// <summary>
    /// Frees all unmanaged per-op memory. String results must be read via
    /// <see cref="GetString"/> BEFORE disposing (they are freed with the ret slots).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var e in _entries)
        {
            if (e.Ret != null)
            {
                // Free any UNREAD native string return first (best effort: caller was
                // supposed to read it; leaking the mono buffer would be worse).
                if (e.Ret->Type == TideType.String && e.Ret->Data.Str.Utf8 != null)
                {
                    e.Ret->FreeNativeReturn();
                }
                Marshal.FreeHGlobal((IntPtr)e.Ret);
            }
            if (e.Args != null)
            {
                // Any arg string buffers left (a flush never happened) are HGlobal-owned.
                for (int i = 0; i < e.Req->ArgCount; i++)
                {
                    if (e.Args[i].Type == TideType.String && e.Args[i].Data.Str.Utf8 != null)
                    {
                        Marshal.FreeHGlobal((IntPtr)e.Args[i].Data.Str.Utf8);
                    }
                }
                Marshal.FreeHGlobal((IntPtr)e.Args);
            }
            Marshal.FreeHGlobal((IntPtr)e.Req);
        }
        _entries.Clear();
    }
}
