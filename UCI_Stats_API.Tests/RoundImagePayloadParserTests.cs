using Microsoft.AspNetCore.Http;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class RoundImagePayloadParserTests
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Theory]
    [InlineData(OnePixelPng)]
    [InlineData("data:image/png;base64," + OnePixelPng)]
    public void ParseAcceptsRawBase64AndPngDataUrl(string imageBase64)
    {
        var result = RoundImagePayloadParser.Parse(new ImportRoundImagePayload(
            imageBase64,
            "route.png",
            "World Cup route",
            1,
            1));

        Assert.NotNull(result);
        Assert.Equal("route.png", result.FileName);
        Assert.Equal("World Cup route", result.AltText);
        Assert.Equal(1, result.WidthPx);
        Assert.Equal(1, result.HeightPx);
        Assert.True(result.Bytes.Length > 8);
    }

    [Fact]
    public void ParseRejectsInvalidBase64()
    {
        var exception = Assert.Throws<ApiException>(() =>
            RoundImagePayloadParser.Parse(new ImportRoundImagePayload(
                "not-base64",
                null,
                null,
                null,
                null)));

        Assert.Equal("ROUND_IMAGE_INVALID_BASE64", exception.Code);
        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
    }

    [Fact]
    public void ParseRejectsNonPngData()
    {
        var exception = Assert.Throws<ApiException>(() =>
            RoundImagePayloadParser.Parse(new ImportRoundImagePayload(
                Convert.ToBase64String("not a png"u8.ToArray()),
                null,
                null,
                null,
                null)));

        Assert.Equal("ROUND_IMAGE_NOT_PNG", exception.Code);
    }

    [Fact]
    public void ParseRejectsInvalidDimensionsBeforeImport()
    {
        var exception = Assert.Throws<ApiException>(() =>
            RoundImagePayloadParser.Parse(new ImportRoundImagePayload(
                OnePixelPng,
                null,
                null,
                0,
                1)));

        Assert.Equal("ROUND_IMAGE_WIDTH_INVALID", exception.Code);
    }
}
