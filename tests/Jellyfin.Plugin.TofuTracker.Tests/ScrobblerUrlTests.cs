using Jellyfin.Plugin.TofuTracker.Core;

namespace Jellyfin.Plugin.TofuTracker.Tests;

public class ScrobblerUrlTests
{
    [Theory]
    [InlineData(null, "https://scrobble.tofutracker.com")]
    [InlineData("", "https://scrobble.tofutracker.com")]
    [InlineData("   ", "https://scrobble.tofutracker.com")]
    [InlineData("https://scrobble.example.org", "https://scrobble.example.org")]
    [InlineData("https://scrobble.example.org/", "https://scrobble.example.org")]
    [InlineData("  https://scrobble.example.org/base/  ", "https://scrobble.example.org/base")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080/", "http://127.0.0.1:8080")]
    public void Valid_urls_are_normalised(string? input, string expected)
    {
        Assert.True(ScrobblerUrl.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("http://scrobble.example.org")]
    [InlineData("ftp://scrobble.example.org")]
    [InlineData("scrobble.example.org")]
    [InlineData("https://user:pass@scrobble.example.org")]
    [InlineData("https://scrobble.example.org/?x=1")]
    [InlineData("https://scrobble.example.org/#frag")]
    public void Unsafe_or_malformed_urls_are_rejected_and_fall_back_to_the_default(string input)
    {
        Assert.False(ScrobblerUrl.TryNormalize(input, out _));
        Assert.Equal(ScrobblerUrl.Default, ScrobblerUrl.NormalizeOrDefault(input));
    }

    [Fact]
    public void Combine_joins_without_double_slashes()
    {
        Assert.Equal("https://a.example/base/v1/events", ScrobblerUrl.Combine("https://a.example/base/", "/v1/events").ToString());
    }
}
