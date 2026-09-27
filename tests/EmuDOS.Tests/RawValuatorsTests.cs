using EmuDOS.Core.Input;

namespace EmuDOS.Tests;

/// <summary>
/// XInput2 packs one value per SET mask bit, in ascending valuator order. The mouse axes are
/// valuators 0 (X) and 1 (Y); a wheel shows up as valuator 2/3 and must never leak into the delta.
/// </summary>
public class RawValuatorsTests
{
    [Fact]
    public void BothAxes_ReadInOrder()
    {
        byte[] mask = [0b0000_0011];
        Assert.True(RawValuators.TryExtractXY(mask, [12.5, -3.0], out var dx, out var dy));
        Assert.Equal(12.5, dx);
        Assert.Equal(-3.0, dy);
    }

    [Fact]
    public void OnlyY_LeavesXZero()
    {
        byte[] mask = [0b0000_0010];
        Assert.True(RawValuators.TryExtractXY(mask, [7.0], out var dx, out var dy));
        Assert.Equal(0.0, dx);
        Assert.Equal(7.0, dy);
    }

    [Fact]
    public void WheelValuators_AreIgnored()
    {
        // X set, Y clear, valuators 2 and 3 (scroll) set: values are packed [x, scroll2, scroll3].
        byte[] mask = [0b0000_1101];
        Assert.True(RawValuators.TryExtractXY(mask, [4.0, 120.0, -120.0], out var dx, out var dy));
        Assert.Equal(4.0, dx);
        Assert.Equal(0.0, dy);
    }

    [Fact]
    public void ScrollOnly_ReportsNoAxis()
    {
        byte[] mask = [0b0000_0100];
        Assert.False(RawValuators.TryExtractXY(mask, [120.0], out var dx, out var dy));
        Assert.Equal(0.0, dx);
        Assert.Equal(0.0, dy);
    }

    [Fact]
    public void EmptyMask_ReportsNoAxis()
    {
        Assert.False(RawValuators.TryExtractXY([0, 0], [], out _, out _));
    }

    [Fact]
    public void FewerValuesThanBits_DoesNotOverrun()
    {
        byte[] mask = [0b0000_0011];
        Assert.True(RawValuators.TryExtractXY(mask, [9.0], out var dx, out var dy));
        Assert.Equal(9.0, dx);
        Assert.Equal(0.0, dy);
    }
}
