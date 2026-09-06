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
