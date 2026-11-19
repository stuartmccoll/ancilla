namespace Ancilla.Core.Tests;

public class HaloIdsTests
{
    [Theory]
    [InlineData("000ba771-0000-4000-8000-000000000001", true)]
    [InlineData("EDFEF3AC-9CBE-4FA2-B949-8F29DEAFD483", true)]
    [InlineData("000ba7710000400080000000000000001", false)]   // no hyphens
    [InlineData("{000ba771-0000-4000-8000-000000000001}", false)]
    [InlineData("000ba771-0000-4000-8000-000000000001/../../users/me", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Guids_must_be_in_plain_hyphenated_form(string? id, bool ok) => Assert.Equal(ok, HaloIds.IsGuid(id));

    [Theory]
    [InlineData("12345", true)]
    [InlineData("18446744073709551615", true)]   // ulong.MaxValue
    [InlineData("18446744073709551616", false)]  // one more
    [InlineData("-1", false)]
    [InlineData("+1", false)]
    [InlineData(" 1", false)]
    [InlineData("1e5", false)]
    [InlineData("12)/../me", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Xuids_are_plain_decimal_digits(string? xuid, bool ok) => Assert.Equal(ok, HaloIds.IsXuid(xuid));

    [Fact]
    public void Require_returns_the_id_or_throws_an_argument_exception()
    {
        Assert.Equal("42", HaloIds.RequireXuid("42"));
        Assert.Equal("000ba771-0000-4000-8000-000000000001", HaloIds.RequireGuid("000ba771-0000-4000-8000-000000000001", "matchId"));
        var ex = Assert.Throws<ArgumentException>(() => HaloIds.RequireGuid("not-a-guid", "matchId"));
        Assert.Equal("matchId", ex.ParamName);
        Assert.StartsWith("Not a valid matchId.", ex.Message);
        Assert.Throws<ArgumentException>(() => HaloIds.RequireXuid("abc"));
    }
}
