using System.Runtime.InteropServices;
using System.Text;

namespace Nami;

/// <summary>Tide value type tags (mirror native tide_abi.h).</summary>
public enum TideType : int
{
    Void = 0,
    I32 = 1,
    I64 = 2,
    R4 = 3,
    R8 = 4,
    Bool = 5,
    String = 6,
    Object = 7
}

/// <summary>Native opcodes (mirror native tide_abi.h).</summary>
internal enum TideCallOp : int
{
    GetStaticField = 1,
    SetStaticField = 2,
    InvokeStatic = 3,
    InvokeInstance = 4,
    GetInstanceField = 5,
    SetInstanceField = 6,
    NewObject = 7,
    FreeHandle = 8,
    ArrayLength = 9,
    ArrayGet = 10,
    ArraySet = 11,
    FindObject = 12
}

/// <summary>
/// Maps CLR types to <see cref="TideType"/> tags for the generic typed API.
/// Enums map to their underlying integer kind (I32 unless the underlying type is long).
/// </summary>
public static class TideTypes
{
    public static TideType Of<T>()
    {
        var t = typeof(T);
        if (t.IsEnum)
        {
            return Type.GetTypeCode(Enum.GetUnderlyingType(t)) == TypeCode.Int64 ? TideType.I64 : TideType.I32;
        }

        return Type.GetTypeCode(t) switch
        {
            TypeCode.Int32 => TideType.I32,
            TypeCode.Int64 => TideType.I64,
            TypeCode.Single => TideType.R4,
            TypeCode.Double => TideType.R8,
            TypeCode.Boolean => TideType.Bool,
            TypeCode.String => TideType.String,
            _ when typeof(GameObject).IsAssignableFrom(t) => TideType.Object,
            _ => throw new NotSupportedException($"type {t} is not supported by Tide")
        };
    }
}

/// <summary>
/// A typed value exchanged with the game. Mirrors the native TideValue layout:
/// a type tag + 8-byte union. Strings are carried as a pointer to a UTF-8 buffer that the
/// caller must keep pinned for the duration of the call.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct TideValue
{
    public TideType Type;
    public ValueUnion Data;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public unsafe struct ValueUnion
    {
        [FieldOffset(0)] public int I32;
        [FieldOffset(0)] public long I64;
        [FieldOffset(0)] public float R4;
        [FieldOffset(0)] public double R8;
        [FieldOffset(0)] public int Bool;
        [FieldOffset(0)] public long Handle;
        [FieldOffset(0)] public StringRef Str;

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct StringRef
        {
            public byte* Utf8;
            public int Len;
        }
    }

    public bool IsString => Type == TideType.String;
    public bool IsObject => Type == TideType.Object;

    public int Int32 => Data.I32;
    public long Int64 => Data.I64;
    public float Single => Data.R4;
    public double Double => Data.R8;
    public bool Boolean => Data.Bool != 0;
    public long Handle => Data.Handle;

    /// <summary>For String returns: the native UTF-8 pointer (must be freed with Tide.FreeUtf8).</summary>
    internal byte* Utf8Ptr => Type == TideType.String ? Data.Str.Utf8 : null;

    public string? String
    {
        get
        {
            if (Type != TideType.String || Data.Str.Utf8 == null)
            {
                return null;
            }
            var s = Marshal.PtrToStringUTF8((IntPtr)Data.Str.Utf8, Data.Str.Len);
            return s;
        }
    }

    public static TideValue FromInt(int v) => new() { Type = TideType.I32, Data = new ValueUnion { I32 = v } };
    public static TideValue FromLong(long v) => new() { Type = TideType.I64, Data = new ValueUnion { I64 = v } };
    public static TideValue FromFloat(float v) => new() { Type = TideType.R4, Data = new ValueUnion { R4 = v } };
    public static TideValue FromDouble(double v) => new() { Type = TideType.R8, Data = new ValueUnion { R8 = v } };
    public static TideValue FromBool(bool v) => new() { Type = TideType.Bool, Data = new ValueUnion { Bool = v ? 1 : 0 } };
    public static TideValue FromHandle(long h) => new() { Type = TideType.Object, Data = new ValueUnion { Handle = h } };

    public static TideValue FromString(string? s)
    {
        var v = new TideValue { Type = TideType.String, Data = new ValueUnion() };
        if (s is null)
        {
            v.Data.Str.Utf8 = null;
            v.Data.Str.Len = 0;
            return v;
        }
        var bytes = Encoding.UTF8.GetBytes(s);
        var buf = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, buf, bytes.Length);
        Marshal.WriteByte(buf, bytes.Length, 0);
        v.Data.Str.Utf8 = (byte*)buf;
        v.Data.Str.Len = bytes.Length;
        return v;
    }

    /// <summary>
    /// Frees a NATIVE string buffer returned by an op (mono_string_to_utf8 result).
    /// Must be called after reading a string return value.
    /// </summary>
    public void FreeNativeReturn()
    {
        if (Type == TideType.String && Data.Str.Utf8 != null)
        {
            Tide.NativeFreeFor(Tide.ActiveBackend, Data.Str.Utf8);
            Data.Str.Utf8 = null;
            Data.Str.Len = 0;
        }
    }

    /// <summary>Frees a string buffer created by <see cref="FromString"/> (AllocHGlobal).</summary>
    public void FreeStringBuffer()
    {
        if (Type == TideType.String && Data.Str.Utf8 != null)
        {
            Marshal.FreeHGlobal((IntPtr)Data.Str.Utf8);
            Data.Str.Utf8 = null;
        }
    }
}

/// <summary>
/// Mirrors native CallRequest. Pointers (args/ret/name buffers) are set by the caller and
/// must remain valid for the duration of the blocking native call.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CallRequest
{
    public fixed byte Assembly[160];
    public fixed byte Ns[160];
    public fixed byte Klass[160];
    public fixed byte Member[160];
    public TideCallOp Op;
    public TideValue* Args;
    public int ArgCount;
    public TideValue* Ret;
    public int HandleCapacity;
    public int ResultCode;
    public fixed byte ErrorMessage[512];
}
