namespace Nami;

/// <summary>
/// Typed game access through Tide.
///
/// A <see cref="GameClass"/> resolves a game class once (by assembly/namespace/name) and
/// offers typed static field access and static method calls. Object instances are exchanged
/// as <see cref="GameObject"/> handles (opaque 64-bit values backed by Mono GCHandles on the
/// game side), which can be passed back in for instance calls and field access.
///
/// Every call blocks until the game's main thread has executed it (see Tide docs).
/// </summary>
public sealed unsafe class GameClass
{
    internal readonly string Assembly;
    internal readonly string Namespace;
    internal readonly string Name;

    internal GameClass(string assembly, string ns, string name)
    {
        Assembly = assembly;
        Namespace = ns;
        Name = name;
    }

    /// <summary>Resolves a game class by assembly (with or without .dll), namespace and name.</summary>
    public static GameClass Resolve(string assembly, string ns, string name) => new(assembly, ns, name);

    // ------------------------------------------------------------- static fields

    public int GetStaticInt(string field) => Call(TideCallOp.GetStaticField, field, TideType.I32).Int32;
    public long GetStaticLong(string field) => Call(TideCallOp.GetStaticField, field, TideType.I64).Int64;
    public float GetStaticFloat(string field) => Call(TideCallOp.GetStaticField, field, TideType.R4).Single;
    public double GetStaticDouble(string field) => Call(TideCallOp.GetStaticField, field, TideType.R8).Double;
    public bool GetStaticBool(string field) => Call(TideCallOp.GetStaticField, field, TideType.Bool).Boolean;

    public string? GetStaticString(string field)
    {
        var v = Call(TideCallOp.GetStaticField, field, TideType.String);
        try
        {
            return v.String;
        }
        finally
        {
            v.FreeNativeReturn();
        }
    }

    /// <summary>Reads a static field/property that holds a UnityEngine.Object; caller owns the handle.</summary>
    public GameObject GetStaticObject(string field)
    {
        var v = Call(TideCallOp.GetStaticField, field, TideType.Object);
        return v.Handle == 0 ? null! : new GameObject(v.Handle);
    }

    public void SetStaticInt(string field, int value) => SetStatic(field, TideValue.FromInt(value));
    public void SetStaticLong(string field, long value) => SetStatic(field, TideValue.FromLong(value));
    public void SetStaticFloat(string field, float value) => SetStatic(field, TideValue.FromFloat(value));
    public void SetStaticDouble(string field, double value) => SetStatic(field, TideValue.FromDouble(value));
    public void SetStaticBool(string field, bool value) => SetStatic(field, TideValue.FromBool(value));
    public void SetStaticString(string field, string? value) => SetStatic(field, TideValue.FromString(value));
    public void SetStaticObject(string field, GameObject? value) => SetStatic(field, TideValue.FromHandle(value?.HandleValue ?? 0));

    private void SetStatic(string field, TideValue value)
    {
        var args = new[] { value };
        unsafe
        {
            fixed (TideValue* p = args)
            {
                TideObjectOp.Call(TideCallOp.SetStaticField, this, field, p, 1, TideType.Void);
            }
        }
    }

    // ------------------------------------------------------------- static calls

    public void CallStatic(string method) => CallStaticVoid(method, Array.Empty<TideValue>());
    public void CallStatic(string method, TideValue a0) => CallStaticVoid(method, new[] { a0 });
    public void CallStatic(string method, TideValue a0, TideValue a1) => CallStaticVoid(method, new[] { a0, a1 });
    public void CallStatic(string method, TideValue a0, TideValue a1, TideValue a2) => CallStaticVoid(method, new[] { a0, a1, a2 });

    public unsafe void CallStaticVoid(string method, TideValue[] args)
    {
        fixed (TideValue* p = args)
        {
            TideObjectOp.Call(TideCallOp.InvokeStatic, this, method, p, args.Length, TideType.Void);
        }
    }

    public unsafe TideValue CallStaticValue(string method, TideValue[] args, TideType returnType)
    {
        fixed (TideValue* p = args)
        {
            return TideObjectOp.Call(TideCallOp.InvokeStatic, this, method, p, args.Length, returnType);
        }
    }

    // ------------------------------------------------------------- instances

    public GameObject NewObject()
    {
        var v = TideObjectOp.Call(TideCallOp.NewObject, this, ".ctor", null, 0, TideType.Object);
        return v.Handle == 0 ? null! : new GameObject(v.Handle);
    }

    private TideValue Call(TideCallOp op, string member, TideType returnType)
    {
        return TideObjectOp.Call(op, this, member, null, 0, returnType);
    }
}

/// <summary>An opaque handle to a live game object (backed by a Mono GCHandle).</summary>
public sealed unsafe class GameObject : IDisposable
{
    /// <summary>The opaque handle value (for diagnostics / re-wrapping).</summary>
    public long HandleValue => Handle;

    internal long Handle { get; private set; }

    internal GameObject(long handle) => Handle = handle;

    /// <summary>Wraps an existing raw handle (e.g. one obtained via <see cref="TideValue.Handle"/>).</summary>
    public static GameObject FromHandle(long handle) => handle == 0 ? null! : new GameObject(handle);

    /// <summary>True once <see cref="Dispose"/> has been called.</summary>
    public bool IsDisposed => Handle == 0;

    public void Call(string method) => CallVoid(method, Array.Empty<TideValue>());
    public void Call(string method, TideValue a0) => CallVoid(method, new[] { a0 });
    public void Call(string method, TideValue a0, TideValue a1) => CallVoid(method, new[] { a0, a1 });

    public void CallVoid(string method, TideValue[] args)
    {
        ThrowIfDisposed();
        var withThis = new TideValue[args.Length + 1];
        withThis[0] = TideValue.FromHandle(Handle);
        Array.Copy(args, 0, withThis, 1, args.Length);
        unsafe
        {
            fixed (TideValue* p = withThis)
            {
                TideObjectOp.CallInstance(TideCallOp.InvokeInstance, Handle, method, p, withThis.Length, TideType.Void);
            }
        }
    }

    // ------------------------------------------------------- typed instance methods

    public int CallIntMethod(string method) => CallMethod(method, Array.Empty<TideValue>(), TideType.I32).Int32;
    public int CallIntMethod(string method, TideValue a0) => CallMethod(method, new[] { a0 }, TideType.I32).Int32;
    public long CallLongMethod(string method) => CallMethod(method, Array.Empty<TideValue>(), TideType.I64).Int64;
    public float CallFloatMethod(string method) => CallMethod(method, Array.Empty<TideValue>(), TideType.R4).Single;
    public double CallDoubleMethod(string method) => CallMethod(method, Array.Empty<TideValue>(), TideType.R8).Double;
    public bool CallBoolMethod(string method) => CallMethod(method, Array.Empty<TideValue>(), TideType.Bool).Boolean;

    public string? CallStringMethod(string method)
    {
        var v = CallMethod(method, Array.Empty<TideValue>(), TideType.String);
        try
        {
            return v.String;
        }
        finally
        {
            v.FreeNativeReturn();
        }
    }

    /// <summary>Calls an instance method returning a UnityEngine.Object; caller owns the handle.</summary>
    public GameObject CallObjectMethod(string method)
    {
        var v = CallMethod(method, Array.Empty<TideValue>(), TideType.Object);
        return v.Handle == 0 ? null! : new GameObject(v.Handle);
    }

    private TideValue CallMethod(string method, TideValue[] args, TideType returnType)
    {
        ThrowIfDisposed();
        var withThis = new TideValue[args.Length + 1];
        withThis[0] = TideValue.FromHandle(Handle);
        Array.Copy(args, 0, withThis, 1, args.Length);
        unsafe
        {
            fixed (TideValue* p = withThis)
            {
                return TideObjectOp.CallInstance(TideCallOp.InvokeInstance, Handle, method, p, withThis.Length, returnType);
            }
        }
    }

    // ------------------------------------------------------- instance fields/properties

    public int GetInt(string field) => GetInstance(TideCallOp.GetInstanceField, field, TideType.I32).Int32;
    public long GetLong(string field) => GetInstance(TideCallOp.GetInstanceField, field, TideType.I64).Int64;
    public float GetFloat(string field) => GetInstance(TideCallOp.GetInstanceField, field, TideType.R4).Single;
    public double GetDouble(string field) => GetInstance(TideCallOp.GetInstanceField, field, TideType.R8).Double;
    public bool GetBool(string field) => GetInstance(TideCallOp.GetInstanceField, field, TideType.Bool).Boolean;

    public string? GetString(string field)
    {
        var v = GetInstance(TideCallOp.GetInstanceField, field, TideType.String);
        try
        {
            return v.String;
        }
        finally
        {
            v.FreeNativeReturn();
        }
    }

    /// <summary>Reads an instance field/property holding a UnityEngine.Object; caller owns the handle.</summary>
    public GameObject GetObject(string field)
    {
        var v = GetInstance(TideCallOp.GetInstanceField, field, TideType.Object);
        return v.Handle == 0 ? null! : new GameObject(v.Handle);
    }

    public void SetInt(string field, int value) => SetField(field, TideValue.FromInt(value));
    public void SetLong(string field, long value) => SetField(field, TideValue.FromLong(value));
    public void SetFloat(string field, float value) => SetField(field, TideValue.FromFloat(value));
    public void SetDouble(string field, double value) => SetField(field, TideValue.FromDouble(value));
    public void SetBool(string field, bool value) => SetField(field, TideValue.FromBool(value));
    public void SetString(string field, string? value) => SetField(field, TideValue.FromString(value));
    public void SetObject(string field, GameObject? value) => SetField(field, TideValue.FromHandle(value?.HandleValue ?? 0));

    private void SetField(string field, TideValue value)
    {
        ThrowIfDisposed();
        var args = new[] { TideValue.FromHandle(Handle), value };
        unsafe
        {
            fixed (TideValue* p = args)
            {
                TideObjectOp.CallInstance(TideCallOp.SetInstanceField, Handle, field, p, 2, TideType.Void);
            }
        }
    }

    private TideValue GetInstance(TideCallOp op, string member, TideType returnType)
    {
        ThrowIfDisposed();
        var args = new[] { TideValue.FromHandle(Handle) };
        unsafe
        {
            fixed (TideValue* p = args)
            {
                return TideObjectOp.CallInstance(op, Handle, member, p, 1, returnType);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Handle == 0)
        {
            throw new ObjectDisposedException(nameof(GameObject));
        }
    }

    /// <summary>Frees the underlying Mono GCHandle. Idempotent and safe to call twice.</summary>
    public void Dispose()
    {
        if (Handle == 0)
        {
            return;
        }

        var handle = Handle;
        Handle = 0;
        if (!Tide.IsAvailable)
        {
            return;  // not in a game process; nothing to free
        }

        var args = new[] { TideValue.FromHandle(handle) };
        unsafe
        {
            fixed (TideValue* p = args)
            {
                TideObjectOp.CallInstance(TideCallOp.FreeHandle, handle, "", p, 1, TideType.Void);
            }
        }
    }
}
