using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class UciResultsPdfParserTests
{
    [Theory]
    [InlineData("BBBBRRRRUUUUNNNNIIII", "BRUNI")]
    [InlineData("3333::::33331111....444466664444", "3:31.464")]
    [InlineData("10007544358", "10007544358")]
    [InlineData("COMMENCAL/MUC-OFF", "COMMENCAL/MUC-OFF")]
    public void NormalizesRepeatedEmbeddedGlyphsOnlyWhenTheWholeTokenUsesThatEncoding(
        string source,
        string expected)
    {
        Assert.Equal(expected, UciResultsPdfParser.NormalizeEmbeddedText(source));
    }
}
