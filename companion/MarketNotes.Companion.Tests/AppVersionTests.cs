using MarketNotes.Companion;
using Xunit;

namespace MarketNotes.Companion.Tests;

public class AppVersionTests
{
    [Fact]
    public void Release_label_is_major_minor_patch()
    {
        Assert.Equal("1.0.0", AppVersion.Format("1.0.0+9b02d92abcdef", null));
        Assert.Equal("1.2.3", AppVersion.Format("1.2.3", null));
        Assert.Equal("1.0.0", AppVersion.Format(null, new Version(1, 0, 0)));
        Assert.Equal("2.0.0-dev", AppVersion.Release("2.0.0-dev+abc"));
    }

    [Fact]
    public void A_release_is_newer_only_when_its_number_is_greater()
    {
        Assert.True(AppVersion.IsNewer("1.0.1", "1.0.0"));
        Assert.True(AppVersion.IsNewer("1.1.0", "1.0.9"));
        Assert.True(AppVersion.IsNewer("2.0.0", "1.9.9"));
        Assert.False(AppVersion.IsNewer("1.0.0", "1.0.0"));
        Assert.False(AppVersion.IsNewer("1.0.0", "1.0.1"));
        Assert.False(AppVersion.IsNewer("nope", "1.0.0"));
    }
}
