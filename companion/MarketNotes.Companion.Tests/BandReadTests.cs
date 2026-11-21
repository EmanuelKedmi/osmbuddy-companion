using MarketNotes.Companion.Scanning;
using Xunit;

namespace MarketNotes.Companion.Tests;

public class BandReadTests
{
    [Fact]
    public void Static_band_is_read_again_while_the_label_is_unknown()
    {
        var gate = new BandRead(2000);
        Assert.True(gate.ShouldRead("a", settled: false, nowMs: 0));
        Assert.False(gate.ShouldRead("a", settled: false, nowMs: 500));
        Assert.False(gate.ShouldRead("a", settled: false, nowMs: 1999));
        Assert.True(gate.ShouldRead("a", settled: false, nowMs: 2000));
        Assert.False(gate.ShouldRead("a", settled: false, nowMs: 3000));
        Assert.True(gate.ShouldRead("a", settled: false, nowMs: 4100));
    }

    [Fact]
    public void Static_band_is_not_read_again_once_the_label_is_settled()
    {
        var gate = new BandRead(2000);
        Assert.True(gate.ShouldRead("a", settled: false, nowMs: 0));
        Assert.False(gate.ShouldRead("a", settled: true, nowMs: 60_000));
    }

    [Fact]
    public void Changed_pixels_are_always_read()
    {
        var gate = new BandRead(2000);
        Assert.True(gate.ShouldRead("a", settled: true, nowMs: 0));
        Assert.True(gate.ShouldRead("b", settled: true, nowMs: 10));
        Assert.True(gate.ShouldRead("a", settled: true, nowMs: 20));
    }

    [Fact]
    public void Reset_reads_the_same_pixels_again()
    {
        var gate = new BandRead(2000);
        Assert.True(gate.ShouldRead("a", settled: true, nowMs: 0));
        gate.Reset();
        Assert.True(gate.ShouldRead("a", settled: true, nowMs: 10));
    }
}
