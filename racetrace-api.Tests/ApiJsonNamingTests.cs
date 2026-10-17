using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Tests;

public sealed class ApiJsonNamingTests
{
    private static JsonSerializerOptions HttpOptions()
    {
        using var services = new ServiceCollection().AddLogging().AddRaceTraceHttp().BuildServiceProvider();
        return services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
    }

    [Fact]
    public void DynamicResponsesUseCamelCaseWithoutMutatingDatabaseRows()
    {
        var row = JsonNode.Parse("""{"rider_id":42,"race_time_ms":123456,"round_categories":[{"round_id":8,"top_speed":52.125}],"missing_value":null}""")!.AsObject();
        var before = row.ToJsonString();

        var response = JsonSerializer.SerializeToNode(row, HttpOptions())!;

        Assert.Equal(42, response["riderId"]!.GetValue<int>());
        Assert.Equal(123456, response["raceTimeMs"]!.GetValue<int>());
        Assert.Equal(52.125m, response["roundCategories"]![0]!["topSpeed"]!.GetValue<decimal>());
        Assert.True(response.AsObject().ContainsKey("missingValue"));
        Assert.Equal(before, row.ToJsonString());
    }

    [Fact]
    public void ProviderPayloadsAreReadWithoutRewritingTheirKeys()
    {
        var payload = JsonSerializer.Deserialize<JsonObject>("""{"RaceTime":123,"rider_id":42,"Times":[{"SplitIndex":1}]}""", HttpOptions())!;
        Assert.Equal(123, payload["RaceTime"]!.GetValue<int>());
        Assert.Equal(42, payload["rider_id"]!.GetValue<int>());
        Assert.Equal(1, payload["Times"]![0]!["SplitIndex"]!.GetValue<int>());
    }

    [Fact]
    public void DictionaryIdentifiersAreDataRatherThanPropertyNames()
    {
        var values = new Dictionary<string, int> { ["elite_men"] = 1, ["EM"] = 2 };
        var response = JsonSerializer.SerializeToNode(values, HttpOptions())!;
        Assert.Equal(1, response["elite_men"]!.GetValue<int>());
        Assert.Equal(2, response["EM"]!.GetValue<int>());
    }

    [Fact]
    public void MvcAndOpenApiUseTheSameCamelCasePolicy()
    {
        using var services = new ServiceCollection().AddLogging().AddRaceTraceHttp().BuildServiceProvider();
        var mvc = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
        var http = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        Assert.Equal("raceTimeMs", mvc.PropertyNamingPolicy!.ConvertName("RaceTimeMs"));
        Assert.Equal("raceTimeMs", http.PropertyNamingPolicy!.ConvertName("RaceTimeMs"));
    }

    [Fact]
    public void InvalidProviderDocumentShapeStillFailsModelBinding() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JsonObject>("[]", HttpOptions()));

    [Fact]
    public void AdminPatchRejectsAmbiguousNamesRatherThanOverwritingAField()
    {
        var patch = JsonNode.Parse("""{"countryCode":"FRA","country_code":"USA"}""")!.AsObject();
        var error = Assert.Throws<ApiException>(() => AdminJsonFields.ToDatabasePatch(patch));
        Assert.Equal("DUPLICATE_ADMIN_FIELD", error.Code);
        Assert.Equal(2, patch.Count);
    }

    [Fact]
    public void EditableResourcesAdvertiseTheCanonicalPatchFieldNames()
    {
        var resources = JsonNode.Parse("""{"competition-classes":["is_active"],"tracks":["country_code","length_m"]}""")!.AsObject();
        var result = AdminJsonFields.EditableResources(resources);
        Assert.Equal("isActive", result["competition-classes"]![0]!.GetValue<string>());
        Assert.Equal("countryCode", result["tracks"]![0]!.GetValue<string>());
        Assert.Equal("lengthM", result["tracks"]![1]!.GetValue<string>());
    }
}
