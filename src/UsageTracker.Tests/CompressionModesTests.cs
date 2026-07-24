using UsageTracker.Contracts;

namespace UsageTracker.Tests;

public class CompressionModesTests
{
    [Theory]
    [InlineData("local")]
    [InlineData("off")]
    [InlineData("remote")]
    [InlineData("REMOTE")]
    [InlineData("Remote")]
    public void IsValid_accepts_known_modes_case_insensitively(string mode)
    {
        Assert.True(CompressionModes.IsValid(mode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cloud")]
    public void IsValid_rejects_unknown_modes(string? mode)
    {
        Assert.False(CompressionModes.IsValid(mode));
    }

    [Fact]
    public void DaemonConfig_defaults_to_remote_compression()
    {
        Assert.Equal(CompressionModes.Remote, new DaemonConfig().CompressionMode);
    }
}
