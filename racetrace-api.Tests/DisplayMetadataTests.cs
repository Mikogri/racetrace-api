using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Features.Metadata;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Tests;

public sealed class DisplayMetadataTests
{
    private const string Metadata = """
        {"categories":[
          {"code":"EM","name":"Elite Men","shortName":"EM","displayOrder":1,"gender":"male","minAge":19,"maxAge":null},
          {"code":"U23_OPEN","name":"Under 23 Open","shortName":"U23","displayOrder":17,"gender":"open","minAge":17,"maxAge":22,"aliases":["Open U23"]}],
         "sessionTypes":[
          {"code":"Q1","name":"Qualifying 1","shortName":"Quali 1","displayOrder":4,"abbreviation":"Q1","groupName":"Qualifying","aliases":["First Qualifier"]},
          {"code":"SUPER_FINAL","name":"Super Final","shortName":"Super","displayOrder":15,"abbreviation":"SF2","groupName":"Finals","aliases":["Last Run"]}],
         "roundStatuses":[{"code":"postponed","name":"Postponed","shortName":"Delayed","displayOrder":8,"aliases":["delayed"]}],
         "sessionStatuses":[{"code":"review","name":"Under Review","shortName":"Review","displayOrder":7,"aliases":[]}]}
        """;

    [Fact]
    public async Task ReadsOneAnonymousRpcAndPreservesDatabaseLabelsAndFutureCodes()
    {
        using var handler = new MetadataHandler(Metadata);
        using var http = new HttpClient(handler);
        var service = CreateService(http);

        var response = await service.GetAsync(default);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal("POST", handler.Method);
        Assert.Equal("https://example.supabase.co/rest/v1/rpc/get_display_metadata", handler.Url);
        Assert.Equal("{}", handler.Body);
        Assert.Equal("anon", handler.ApiKey);
        Assert.Equal("elite_men", response.Categories[0].Code);
        Assert.Equal("Elite Men", response.Categories[0].Name);
        Assert.Contains("EM", response.Categories[0].Aliases);
        Assert.Contains("ME", response.Categories[0].Aliases);
        Assert.Contains("Men Elite", response.Categories[0].Aliases);
        Assert.Contains("men_elite", response.Categories[0].Aliases);
        var future = response.Categories[1];
        Assert.Equal("u23_open", future.Code);
        Assert.Equal("U23_OPEN", future.DatabaseCode);
        Assert.Equal("Under 23 Open", future.Name);
        Assert.Equal("U23", future.ShortName);
        Assert.Equal(17, future.DisplayOrder);
        Assert.Equal("open", future.Gender);
        Assert.Equal(17, future.MinAge);
        Assert.Equal(22, future.MaxAge);
        Assert.Contains("Open U23", future.Aliases);
        Assert.Contains("U23_OPEN", future.Aliases);
        Assert.Equal("qualifying_1", response.SessionTypes[0].Code);
        Assert.Contains("QUALIFYING1", response.SessionTypes[0].Aliases);
        Assert.Contains("First Qualifier", response.SessionTypes[0].Aliases);
        Assert.Equal("super_final", response.SessionTypes[1].Code);
        Assert.Equal("SUPER_FINAL", response.SessionTypes[1].DatabaseCode);
        Assert.Equal("SF2", response.SessionTypes[1].Abbreviation);
        Assert.Equal("Finals", response.SessionTypes[1].GroupName);
        Assert.Equal("postponed", Assert.Single(response.RoundStatuses).Code);
        Assert.Equal("review", Assert.Single(response.SessionStatuses).Code);
    }

    [Fact]
    public async Task ControllerReturnsCamelCaseWithApplicationJsonSettings()
    {
        using var handler = new MetadataHandler(Metadata);
        using var http = new HttpClient(handler);
        var controller = new MetadataController(CreateService(http));
        var result = Assert.IsType<OkObjectResult>(await controller.Get(default));
        var json = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.True(json.TryGetProperty("sessionTypes", out var sessions));
        Assert.Equal("Quali 1", sessions[0].GetProperty("shortName").GetString());
        Assert.Equal("Q1", sessions[0].GetProperty("abbreviation").GetString());
        Assert.Equal("Qualifying", sessions[0].GetProperty("groupName").GetString());
        var category = json.GetProperty("categories")[0];
        Assert.Equal(1, category.GetProperty("displayOrder").GetInt32());
        Assert.Equal(19, category.GetProperty("minAge").GetInt32());
        Assert.Equal(JsonValueKind.Null, category.GetProperty("maxAge").ValueKind);
        Assert.False(category.TryGetProperty("database_code", out _));
        Assert.False(category.TryGetProperty("databaseCode", out _));
        Assert.True(json.TryGetProperty("roundStatuses", out _));
        Assert.True(json.TryGetProperty("sessionStatuses", out _));
        Assert.False(json.TryGetProperty("session_types", out _));
    }

    [Theory]
    [InlineData("EM", "elite_men", "ME")]
    [InlineData("EW", "elite_women", "WE")]
    [InlineData("JM", "junior_men", "MJ")]
    [InlineData("JW", "junior_women", "WJ")]
    public async Task KeepsExistingCategoryAliases(string databaseCode, string publicCode, string alias)
    {
        using var handler = new MetadataHandler(Metadata.Replace("\"EM\"", $"\"{databaseCode}\""));
        using var http = new HttpClient(handler);
        var response = await CreateService(http).GetAsync(default);
        Assert.Equal(publicCode, response.Categories[0].Code);
        Assert.Contains(alias, response.Categories[0].Aliases);
    }

    [Theory]
    [InlineData("SEEDING", "seeding")]
    [InlineData("QUALI", "qualifying")]
    [InlineData("Q1", "qualifying_1")]
    [InlineData("Q2", "qualifying_2")]
    [InlineData("SEMI", "semi_final")]
    [InlineData("FINAL", "final")]
    [InlineData("TIMED_TRAINING", "timed_training")]
    [InlineData("PRACTICE", "training")]
    public async Task KeepsExistingSessionCodes(string databaseCode, string publicCode)
    {
        using var handler = new MetadataHandler(Metadata.Replace("\"Q1\"", $"\"{databaseCode}\""));
        using var http = new HttpClient(handler);
        var response = await CreateService(http).GetAsync(default);
        Assert.Equal(publicCode, response.SessionTypes[0].Code);
        Assert.Equal(databaseCode, response.SessionTypes[0].DatabaseCode);
        Assert.Contains(databaseCode, response.SessionTypes[0].Aliases);
    }

    [Fact]
    public async Task UpstreamFailureIsNotConvertedToAnEmptySuccessfulDictionary()
    {
        using var handler = new MetadataHandler("schema cache unavailable", HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<SupabaseApiException>(() => CreateService(http).GetAsync(default));
        Assert.Equal(503, error.StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task MissingMetadataIsNotConvertedToAnEmptySuccessfulDictionary(string body)
    {
        using var handler = new MetadataHandler(body);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<JsonException>(() => CreateService(http).GetAsync(default));
    }

    [Fact]
    public async Task PropagatesCancellationToTheRpc()
    {
        using var handler = new MetadataHandler(Metadata);
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService(http).GetAsync(cancellation.Token));
        Assert.True(handler.CancellationRequested);
    }

    [Fact]
    public void RegistersMetadataServiceInTheExistingCompositionRoot()
    {
        var services = new ServiceCollection().AddRaceTraceApplication();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, item => item.ServiceType == typeof(DisplayMetadataService)).Lifetime);
    }

    private static DisplayMetadataService CreateService(HttpClient http) => new(new SupabaseRestClient(http,
        Options.Create(new SupabaseOptions { Url = "https://example.supabase.co", AnonKey = "anon", ServiceRoleKey = "service" })));

    private sealed class MetadataHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Body { get; private set; }
        public string? ApiKey { get; private set; }
        public bool CancellationRequested { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            CancellationRequested = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            Method = request.Method.Method;
            Url = request.RequestUri!.AbsoluteUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            ApiKey = request.Headers.GetValues("apikey").Single();
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
