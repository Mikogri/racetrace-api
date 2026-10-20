using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Controllers;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class ChronoRaceProxyNamingTests
{
    [Fact]
    public async Task ResultProxyPublishesOnlySportingRiderFields()
    {
        using var http = new HttpClient(new ProviderHandler("""
            {"Riders":{"7":{"GivenName":"Test","FamilyName":"Rider","RaceNr":7,"Injury":true,"PrivateVendorField":"discard"}},
             "Results":[{"RaceNr":7,"Status":"DNF","RaceTime":null}]}
            """));
        var payload = await new ChronoRaceLiveProxyService(http).GetResultsAsync("2026_mtb", "final", default);
        var rider = payload!["Riders"]!["7"]!.AsObject();
        Assert.Equal("Test", rider["GivenName"]!.GetValue<string>());
        Assert.Equal(3, rider.Count);
        Assert.Equal("DNF", payload["Results"]![0]!["Status"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProxyPayloadRetainsProviderPropertyNames()
    {
        using var http = new HttpClient(new ProviderHandler());
        var controller = new ChronoRaceLiveController(new ChronoRaceLiveProxyService(http));
        var result = await controller.Catalog("2026_mtb", default);
        using var services = new ServiceCollection().AddLogging().AddRaceTraceHttp().BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var json = result is JsonResult custom
            ? JsonSerializer.SerializeToElement(custom.Value, (JsonSerializerOptions?)custom.SerializerSettings ?? options)
            : JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, options);

        Assert.Equal(1, json.GetProperty("provider_field").GetInt32());
        Assert.Equal(42, json.GetProperty("Riders")[0].GetProperty("rider_id").GetInt32());
    }

    private sealed class ProviderHandler(string? payload = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload ?? """{"provider_field":1,"Riders":[{"rider_id":42}]}""")
            });
    }
}
