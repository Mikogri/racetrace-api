using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class PublicDtoMappingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-id")]
    [InlineData("9223372036854775808")]
    public void InvalidIdentifiersPreserveZeroVersusNullFallback(string? value)
    {
        Assert.Equal(0, PublicDtoMapping.Long(value));
        Assert.Null(PublicDtoMapping.NullableLong(value));
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData(" 42 ", 42L)]
    [InlineData("+42", 42L)]
    [InlineData("-42", -42L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void ValidIdentifiersPreserveTheirValue(string value, long expected)
    {
        Assert.Equal(expected, PublicDtoMapping.Long(value));
        Assert.Equal(expected, PublicDtoMapping.NullableLong(value));
    }
}
