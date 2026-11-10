using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class RoundImagePayloadParserTests
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Theory]
    [InlineData(OnePixelPng)]
    [InlineData("data:image/png;base64," + OnePixelPng)]
    public void ParseAcceptsRawBase64AndPngDataUrl(string imageBase64)
    {
        var result = RoundImagePayloadParser.Parse(
            new ImportRoundImagePayload(imageBase64, "route.png", "World Cup route", 1, 1)
        );

        Assert.NotNull(result);
        Assert.Equal("route.webp", result.FileName);
        Assert.Equal("World Cup route", result.AltText);
        Assert.Equal(1, result.WidthPx);
        Assert.Equal(1, result.HeightPx);
        Assert.True(result.Bytes.Length > 12);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(result.Bytes, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(result.Bytes, 8, 4));
    }

    [Fact]
    public void ParseRejectsInvalidBase64()
    {
        var exception = Assert.Throws<ApiException>(() =>
            RoundImagePayloadParser.Parse(
                new ImportRoundImagePayload("not-base64", null, null, null, null)
            )
        );

        Assert.Equal("ROUND_IMAGE_INVALID_BASE64", exception.Code);
        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
    }

    [Fact]
    public void ParseRejectsNonPngData()
    {
        var exception = Assert.Throws<ApiException>(() =>
            RoundImagePayloadParser.Parse(
                new ImportRoundImagePayload(
                    Convert.ToBase64String("not a png"u8.ToArray()),
                    null,
                    null,
                    null,
                    null
                )
            )
        );

        Assert.Equal("ROUND_IMAGE_NOT_PNG", exception.Code);
    }

    [Fact]
    public void ParseRejectsInvalidDimensionsBeforeImport()
    {
        var exception = Assert.Throws<ApiException>(() =>
            RoundImagePayloadParser.Parse(
                new ImportRoundImagePayload(OnePixelPng, null, null, 0, 1)
            )
        );

        Assert.Equal("ROUND_IMAGE_WIDTH_INVALID", exception.Code);
    }

    [Fact]
    public async Task UploadStoresConvertedWebpWithMatchingContentType()
    {
        var image = RoundImagePayloadParser.Parse(
            new ImportRoundImagePayload(OnePixelPng, "route.png", null, null, null)
        )!;
        using var handler = new UploadHandler();
        using var http = new HttpClient(handler);
        var storage = new RoundImageStorageService(
            http,
            Options.Create(
                new SupabaseOptions
                {
                    Url = "https://storage.example.test",
                    ServiceRoleKey = "test-key",
                }
            )
        );

        var path = await storage.UploadAsync(image, CancellationToken.None);

        Assert.EndsWith(".webp", path);
        Assert.Equal("image/webp", handler.ContentType);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(handler.UploadedBytes!, 0, 4));
    }

    private sealed class UploadHandler : HttpMessageHandler
    {
        public string? ContentType { get; private set; }
        public byte[]? UploadedBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            ContentType = request.Content!.Headers.ContentType?.MediaType;
            UploadedBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
