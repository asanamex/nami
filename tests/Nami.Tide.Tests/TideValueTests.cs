using System.Runtime.InteropServices;
using System.Text;

namespace Nami.Tests;

public unsafe sealed class TideValueTests
{
    [Fact]
    public void FromInt_RoundTrips()
    {
        var v = TideValue.FromInt(42);
        Assert.Equal(TideType.I32, v.Type);
        Assert.Equal(42, v.Int32);
    }

    [Fact]
    public void FromLong_RoundTrips()
    {
        var v = TideValue.FromLong(long.MaxValue);
        Assert.Equal(TideType.I64, v.Type);
        Assert.Equal(long.MaxValue, v.Int64);
    }

    [Fact]
    public void FromBool_RoundTrips()
    {
        Assert.True(TideValue.FromBool(true).Boolean);
        Assert.False(TideValue.FromBool(false).Boolean);
    }

    [Fact]
    public void FromFloat_And_Double_RoundTrip()
    {
        Assert.Equal(1.5f, TideValue.FromFloat(1.5f).Single);
        Assert.Equal(2.5d, TideValue.FromDouble(2.5d).Double);
    }

    [Fact]
    public void FromString_AllocatesUtf8_ThatStringReadsBack()
    {
        var v = TideValue.FromString("héllo wörld ✓");
        try
        {
            Assert.Equal(TideType.String, v.Type);
            Assert.Equal("héllo wörld ✓", v.String);
        }
        finally
        {
            v.FreeStringBuffer();
        }
    }

    [Fact]
    public void FromString_Null_YieldsNullUtf8()
    {
        var v = TideValue.FromString(null);
        Assert.Equal(TideType.String, v.Type);
        Assert.True(v.Data.Str.Utf8 == null);
        Assert.Null(v.String);
        // Freeing a null buffer is a safe no-op.
        v.FreeStringBuffer();
    }

    [Fact]
    public void FromString_ReuseAfterFree_IsSafeNoop()
    {
        var v = TideValue.FromString("x");
        v.FreeStringBuffer();
        v.FreeStringBuffer();  // second free must not throw / double-free
        Assert.True(v.Data.Str.Utf8 == null);
    }

    [Fact]
    public void FromHandle_SetsObjectType()
    {
        var v = TideValue.FromHandle(0x1234);
        Assert.Equal(TideType.Object, v.Type);
        Assert.Equal(0x1234L, v.Handle);
    }

    [Fact]
    public void CallRequest_Layout_MatchesNative()
    {
        // Mirrors native CallRequest natural alignment on x64:
        //   Assembly/Ns/Klass/Member: 4 × 160 = 640
        //   Op(4) + pad(4) + Args(8) + ArgCount(4) + pad(4) + Ret(8) +
        //   HandleCapacity(4) + ResultCode(4) + ErrorMessage(512) = 552
        //   total 1192.
        Assert.Equal(1192, Marshal.SizeOf<CallRequest>());
    }

    [Fact]
    public void TideValue_Size_Is24()
    {
        // 4-byte tag + 16-byte union + 4 pad = 24 on x64 (mirrors native).
        Assert.Equal(24, Marshal.SizeOf<TideValue>());
    }
}

public sealed class TideStringMarshalingTests
{
    [Fact]
    public void Utf8Names_AreNotLossy()
    {
        // The managed Fill/Ansi use UTF-8 now; verify Encoding produces the bytes we expect.
        var bytes = Encoding.UTF8.GetBytes("Ünity.Test");
        Assert.Contains(bytes, b => b > 127);  // non-ASCII preserved, not '?'
        Assert.Equal("Ünity.Test", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void FreeNativeReturn_IsIdempotent()
    {
        // Without a real native buffer we can only verify the guard on a non-string type.
        var v = TideValue.FromInt(1);
        v.FreeNativeReturn();  // must not throw
        v.FreeStringBuffer();  // must not throw
    }
}

public enum TestEnum
{
    Zero = 0,
    One = 1,
    Seven = 7
}

public enum TestLongEnum : long
{
    Zero = 0L,
    Big = 0x1_0000_0001L
}

public sealed class TideTypesTests
{
    [Theory]
    [InlineData(typeof(int), TideType.I32)]
    [InlineData(typeof(long), TideType.I64)]
    [InlineData(typeof(float), TideType.R4)]
    [InlineData(typeof(double), TideType.R8)]
    [InlineData(typeof(bool), TideType.Bool)]
    [InlineData(typeof(string), TideType.String)]
    public void Of_Primitives(Type t, TideType expected)
    {
        var method = typeof(TideTypes).GetMethod(nameof(TideTypes.Of))!.MakeGenericMethod(t);
        Assert.Equal(expected, method.Invoke(null, null));
    }

    [Fact]
    public void Of_Enum_MapsToUnderlyingInt()
    {
        Assert.Equal(TideType.I32, TideTypes.Of<TestEnum>());
    }

    [Fact]
    public void Of_GameObject_MapsToObject()
    {
        Assert.Equal(TideType.Object, TideTypes.Of<GameObject>());
    }
}

public sealed class TideValueConversionTests
{
    [Fact]
    public void ToValue_Int_ProducesI32()
    {
        var v = GameClass.ToValue(42);
        Assert.Equal(TideType.I32, v.Type);
        Assert.Equal(42, v.Int32);
    }

    [Fact]
    public void ToValue_Bool_ProducesBool()
    {
        var v = GameClass.ToValue(true);
        Assert.Equal(TideType.Bool, v.Type);
        Assert.True(v.Boolean);
    }

    [Fact]
    public void ToValue_String_ProducesString()
    {
        var v = GameClass.ToValue("hi");
        try
        {
            Assert.Equal(TideType.String, v.Type);
            Assert.Equal("hi", v.String);
        }
        finally
        {
            v.FreeStringBuffer();
        }
    }

    [Fact]
    public void ToValue_Enum_ProducesUnderlyingInt()
    {
        var v = GameClass.ToValue(TestEnum.Seven);
        Assert.Equal(TideType.I32, v.Type);
        Assert.Equal(7, v.Int32);
    }

    [Fact]
    public void ToValue_GameObject_ProducesHandle()
    {
        var go = GameObject.FromHandle(1234)!;
        var v = GameClass.ToValue(go);
        Assert.Equal(TideType.Object, v.Type);
        Assert.Equal(1234L, v.Handle);
    }

    [Fact]
    public void Convert_Enum_RestoresValue()
    {
        var v = TideValue.FromInt(7);
        var result = GameClass.Convert<TestEnum>(v);
        Assert.Equal(TestEnum.Seven, result);
    }

    [Fact]
    public void Convert_Int_RestoresValue()
    {
        var v = TideValue.FromInt(99);
        Assert.Equal(99, GameClass.Convert<int>(v));
    }

    [Fact]
    public void Convert_Bool_RestoresValue()
    {
        Assert.True(GameClass.Convert<bool>(TideValue.FromBool(true)));
    }

    [Fact]
    public void ToValue_LongEnum_ProducesI64WithoutTruncation()
    {
        var v = GameClass.ToValue(TestLongEnum.Big);
        Assert.Equal(TideType.I64, v.Type);
        Assert.Equal(0x1_0000_0001L, v.Int64);
    }

    [Fact]
    public void Convert_LongEnum_RestoresValue()
    {
        var v = TideValue.FromLong(0x1_0000_0001L);
        Assert.Equal(TestLongEnum.Big, GameClass.Convert<TestLongEnum>(v));
    }

    [Fact]
    public void BorrowedHandle_Dispose_IsNoOpWithoutFree()
    {
        // Borrowed wrappers (hook-frame temporaries) must never issue a FreeHandle:
        // Dispose only marks them disposed. No loader here, so this pins the guard
        // branch, not the native free path (in-game sample proves the latter).
        var go = GameObject.FromBorrowedHandle(1234)!;
        Assert.False(go.IsDisposed);
        go.Dispose();
        Assert.True(go.IsDisposed);
        go.Dispose(); // idempotent
    }
}
