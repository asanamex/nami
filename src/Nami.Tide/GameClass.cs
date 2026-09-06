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

    public void SetStaticInt(string field, int value) => SetStatic(field, TideValue.FromInt(value));
    public void SetStaticLong(string field, long value) => SetStatic(field, TideValue.FromLong(value));
    public void SetStaticFloat(string field, float value) => SetStatic(field, TideValue.FromFloat(value));
    public void SetStaticDouble(string field, double value) => SetStatic(field, TideValue.FromDouble(value));
    public void SetStaticBool(string field, bool value) => SetStatic(field, TideValue.FromBool(value));
    public void SetStaticString(string field, string? value) => SetStatic(field, TideValue.FromString(value));

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
    /// <summary>The opaque handle value (for diagnostics).</summary>
    public long HandleValue => Handle;

    internal long Handle { get; }

    internal GameObject(long handle) => Handle = handle;

    public void Call(string method) => CallVoid(method, Array.Empty<TideValue>());
    public void Call(string method, TideValue a0) => CallVoid(method, new[] { a0 });
    public void Call(string method, TideValue a0, TideValue a1) => CallVoid(method, new[] { a0, a1 });

    /// <summary>Calls a parameterless instance method returning an int.</summary>
    public int CallIntMethod(string method)
    {
        var withThis = new[] { TideValue.FromHandle(Handle) };
        unsafe
        {
            fixed (TideValue* p = withThis)
            {
                return TideObjectOp.CallInstance(TideCallOp.InvokeInstance, Handle, method, p, 1, TideType.I32).Int32;
            }
        }
    }

    public void CallVoid(string method, TideValue[] args)
    {
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

    public int GetInt(string field) => CallInstance(TideCallOp.GetInstanceField, field, TideType.I32).Int32;
    public long GetLong(string field) => CallInstance(TideCallOp.GetInstanceField, field, TideType.I64).Int64;
    public float GetFloat(string field) => CallInstance(TideCallOp.GetInstanceField, field, TideType.R4).Single;
    public double GetDouble(string field) => CallInstance(TideCallOp.GetInstanceField, field, TideType.R8).Double;
    public bool GetBool(string field) => CallInstance(TideCallOp.GetInstanceField, field, TideType.Bool).Boolean;

    public string? GetString(string field)
    {
        var v = CallInstance(TideCallOp.GetInstanceField, field, TideType.String);
        try
        {
            return v.String;
        }
        finally
        {
            v.FreeNativeReturn();
        }
    }

    public void SetInt(string field, int value) => SetField(field, TideValue.FromInt(value));
    public void SetLong(string field, long value) => SetField(field, TideValue.FromLong(value));
    public void SetFloat(string field, float value) => SetField(field, TideValue.FromFloat(value));
    public void SetDouble(string field, double value) => SetField(field, TideValue.FromDouble(value));
    public void SetBool(string field, bool value) => SetField(field, TideValue.FromBool(value));
    public void SetString(string field, string? value) => SetField(field, TideValue.FromString(value));

    private void SetField(string field, TideValue value)
    {
        var args = new[] { TideValue.FromHandle(Handle), value };
        unsafe
        {
            fixed (TideValue* p = args)
            {
                TideObjectOp.CallInstance(TideCallOp.SetInstanceField, Handle, field, p, 2, TideType.Void);
            }
        }
    }

    private TideValue CallInstance(TideCallOp op, string member, TideType returnType)
    {
        // Instance field ops need args[0] = the handle.
        var args = new[] { TideValue.FromHandle(Handle) };
        unsafe
        {
            fixed (TideValue* p = args)
            {
                return TideObjectOp.CallInstance(op, Handle, member, p, 1, returnType);
            }
        }
    }

    public void Dispose()
    {
        var args = new[] { TideValue.FromHandle(Handle) };
        unsafe
        {
            fixed (TideValue* p = args)
            {
                TideObjectOp.CallInstance(TideCallOp.FreeHandle, Handle, "", p, 1, TideType.Void);
            }
        }
    }
}
