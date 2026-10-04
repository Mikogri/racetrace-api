using System.Text.Json.Nodes;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RaceTrace.Api.Features.Fantasy;
using RaceTrace.Api.Features.Fantasy.Import;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class FantasyTrainingParticipantsTests
{
    [Fact]
    public async Task ChronoUsesWholeTrainingRosterNotPartialFinishersAndIgnoresQualifying()
    {
        var riders = Enumerable.Range(1, 4).Select(id => Rider(id.ToString(), $"Rider {id}", "male")).ToArray();
        var roster = new JsonObject(riders.Select(rider => new KeyValuePair<string, JsonNode?>(rider.RiderId!,
            new JsonObject { ["GivenName"] = "Rider", ["FamilyName"] = rider.RiderId })));
        var handler = new Responses(url => url.Contains("/cms/")
            ? """{"Childs":[{"Name":"DHI","Childs":[{"Name":"ME","Childs":[{"Name":"Timed Training","Childs":[{"Route":"{\"Params\":{\"key\":\"TT_ME\"}}"}]},{"Name":"Final","Childs":[{"Route":"{\"Params\":{\"key\":\"FINAL\"}}"}]}]}]}]}"""
            : new JsonObject { ["Riders"] = roster.DeepClone(), ["Results"] = new JsonArray(new JsonObject { ["RaceNr"] = 1 }) }.ToJsonString());
        var service = Create(handler);
        var fields = await service.ResolveAsync(LiveRound("world_cup"), riders, null, default);
        Assert.Equal(4, Assert.Single(fields).RiderIds.Count);
        Assert.DoesNotContain(handler.Urls, url => url.Contains("FINAL"));
    }

    [Fact]
    public async Task TissotDiscoversTrainingPhaseAndUsesStartListNotFinalResults()
    {
        var handler = new Responses(url => url.Contains("?year=") ? """[{"sport":"MTB","name":"World Championships","code":"mtbwch"}]"""
            : url.EndsWith("/events") ? """[{"number":42,"name":"Women Elite Downhill"}]"""
            : url.EndsWith("/phases") ? """[{"number":7,"name":"Timed Training"},{"number":2,"name":"Final"}]"""
            : """{"results":[{"rider":{"name":"HÖLL Valentina"}},{"rider":{"name":"CABIROU Marine"}}]}""");
        var fields = await Create(handler).ResolveAsync(LiveRound("world_championships"),
            [Rider("1", "Valentina Höll", "female"), Rider("2", "Marine Cabirou", "female")], null, default);
        Assert.Equal(2, Assert.Single(fields).RiderIds.Count);
        Assert.Contains(handler.Urls, url => url.EndsWith("/events/42/phases/7/startlist"));
        Assert.DoesNotContain(handler.Urls, url => url.EndsWith("/results"));
    }

    [Fact]
    public async Task ProviderFailureKeepsEvidenceOnlyForTheSameContext()
    {
        var service = Create(new Responses(_ => throw new HttpRequestException("Unavailable")));
        FantasyTrainingField[] previous = [new("male", "test", "https://example.org/training", DateTimeOffset.UtcNow, ["1"]),
            new("female", "other-round", "https://example.org/training", DateTimeOffset.UtcNow, ["2"])];
        var fields = await service.ResolveAsync(LiveRound("world_cup"), [], previous, default);
        Assert.Equal("test", Assert.Single(fields).Context);
    }

    [Fact]
    public void MatchesReversedNamesAndAccentsWithinTheSameCategory()
    {
        var riders = new[] { Rider("1", "Valentina Höll", "female"), Rider("2", "Loic Bruni", "male") };
        Assert.Equal(new[] { "1" }, FantasyTrainingParticipants.MatchRoster(["HÖLL Valentina", "BRUNI Loic"], riders, "female"));
    }

    [Fact]
    public void NeverGuessesAmbiguousNamesOrUnmappedRiders()
    {
        var riders = new[] { Rider("1", "Alex Smith", "male"), Rider("2", "Alex Smith", "male"), Rider(null, "Loic Bruni", "male") };
        Assert.Empty(FantasyTrainingParticipants.MatchRoster(["SMITH Alex", "BRUNI Loic"], riders, "male"));
    }

    [Fact]
    public void EmptyRosterIsNotAConfirmedEmptyField() =>
        Assert.Empty(FantasyTrainingParticipants.MatchRoster([], [Rider("1", "Loic Bruni", "male")], "male"));

    [Theory]
    [InlineData("live", true)]
    [InlineData("upcoming", false)]
    [InlineData("completed", false)]
    public void OnlyLiveRoundsTriggerExternalRosterDiscovery(string status, bool expected) =>
        Assert.Equal(expected, FantasyTrainingParticipants.IsLive(new JsonObject { ["status"] = status }));

    private static PinkbikeFantasyRider Rider(string? id, string name, string gender) =>
        new(id ?? "external", id, name, gender, 100_000, 0, false, DateTimeOffset.UtcNow, "test", null, []);

    private static JsonObject LiveRound(string competition) => new()
    {
        ["status"] = "live", ["season_year"] = 2026, ["context_name"] = "test",
        ["competition_levels"] = new JsonObject { ["code"] = competition }
    };
    private static FantasyTrainingParticipants Create(Responses handler)
    {
        var http = new HttpClient(handler);
        return new(http, new ChronoRaceLiveProxyService(http), TimeProvider.System, NullLogger<FantasyTrainingParticipants>.Instance);
    }
    private sealed class Responses(Func<string, string> response) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(url)) });
        }
    }
}
