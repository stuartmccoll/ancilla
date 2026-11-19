namespace Ancilla.Core.Tests;

public class IsoDurationTests
{
    [Theory]
    [InlineData("PT15M17.3S", 0, 0, 15, 17, 300)]
    [InlineData("P9DT18H33M4.5S", 9, 18, 33, 4, 500)]
    [InlineData("PT0S", 0, 0, 0, 0, 0)]
    public void Parses_the_services_durations(string iso, int d, int h, int m, int s, int ms) =>
        Assert.Equal(new TimeSpan(d, h, m, s, ms), IsoDuration.Parse(iso));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eleven minutes")]
    public void Bad_or_missing_durations_are_zero(string? iso) => Assert.Equal(TimeSpan.Zero, IsoDuration.Parse(iso));

    [Fact]
    public void Try_parse_only_accepts_iso_durations()
    {
        Assert.True(IsoDuration.TryParse("PT1M", out var t));
        Assert.Equal(TimeSpan.FromMinutes(1), t);
        Assert.False(IsoDuration.TryParse("Master Chief", out _));
        Assert.False(IsoDuration.TryParse("Pelican", out _));   // starts with P but isn't one
        Assert.False(IsoDuration.TryParse(null, out _));
    }
}
