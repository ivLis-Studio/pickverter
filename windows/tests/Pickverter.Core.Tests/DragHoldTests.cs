using Xunit;

namespace Pickverter.Tests;

/// Holding Shift, Ctrl or Alt while dragging files opens the ring once the pointer rests (the Mac self-test checks the
/// same timeline against DragHold.swift).
public class DragHoldTests
{
    private const int Ctrl = 0xA2;
    private const double Slop = 12;

    [Fact]
    public void KeyPressedDuringTheDragOpensOnceThePointerRests()
    {
        var hold = new DragHold(Slop);
        hold.DragStarted(0.0, 100, 100);
        hold.Moved(0.2, 160, 120);
        hold.Press(Ctrl, 0.3, 160, 120);
        Assert.Null(hold.Due(0.5));
        Assert.Null(hold.Due(0.79));
        Assert.Equal(Ctrl, hold.Due(0.81));
        Assert.Null(hold.Due(1.5));
    }

    [Fact]
    public void KeyHeldBeforeTheDragWaitsForTheDragAndARest()
    {
        var hold = new DragHold(Slop);
        hold.Press(Ctrl, 0.0, 100, 100);
        hold.DragStarted(1.0, 106, 100);
        Assert.Null(hold.Due(1.4));
        Assert.Equal(Ctrl, hold.Due(1.5));
    }

    [Fact]
    public void APointerThatKeepsMovingLeavesTheSystemDragAlone()
    {
        var hold = new DragHold(Slop);
        hold.Press(Ctrl, 0.0, 100, 100);
        hold.DragStarted(0.1, 108, 100);
        for (var step = 1; step <= 40; step++)
        {
            var now = 0.1 + step * 0.05;
            hold.Moved(now, 108 + step * 15, 100);
            Assert.Null(hold.Due(now));
        }
        Assert.Null(hold.Due(2.5));
        Assert.Equal(Ctrl, hold.Due(2.61));
    }

    [Fact]
    public void JitterInsideTheSlopStillCountsAsResting()
    {
        var hold = new DragHold(Slop);
        hold.DragStarted(0.0, 100, 100);
        hold.Press(Ctrl, 0.0, 100, 100);
        hold.Moved(0.2, 110, 95);
        hold.Moved(0.4, 92, 108);
        Assert.Equal(Ctrl, hold.Due(0.5));
    }

    [Fact]
    public void ReleasingTheKeyOrAddingAnotherCancels()
    {
        var hold = new DragHold(Slop);
        hold.DragStarted(0.0, 100, 100);
        hold.Press(Ctrl, 0.0, 100, 100);
        hold.Cancel();
        Assert.Null(hold.Due(2.0));
        Assert.Null(hold.Key);
    }

    [Fact]
    public void EveryNewDragCanOpenAgain()
    {
        var hold = new DragHold(Slop);
        hold.Press(Ctrl, 0.0, 100, 100);
        hold.DragStarted(0.1, 108, 100);
        Assert.Equal(Ctrl, hold.Due(0.61));
        Assert.True(hold.Used);
        hold.DragStarted(2.0, 300, 300);
        Assert.False(hold.Used);
        Assert.Null(hold.Due(2.2));
        Assert.Equal(Ctrl, hold.Due(2.5));
    }
}
