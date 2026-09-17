using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UCI_Stats_API.Features.Fantasy.Import;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Features.Fantasy;

public sealed record FantasyTrainingField(string Gender, string Context, string SourceUrl,
    DateTimeOffset FetchedAtUtc, IReadOnlyList<string> RiderIds);

/// <summary>Optional roster evidence. Runs during background preparation, never on public reads.
/// Uses training rosters, not the partial order of finishers or a later qualifying/final field.</summary>
public sealed class FantasyTrainingParticipants(HttpClient http, ChronoRaceLiveProxyService chrono,
    TimeProvider time, ILogger<FantasyTrainingParticipants> logger)
{
    private const string TissotRoot = "https://prod.server.tissottiming.com/competitions";

    public async Task<IReadOnlyList<FantasyTrainingField>> ResolveAsync(JsonObject round,
        IReadOnlyList<PinkbikeFantasyRider> riders, IReadOnlyList<FantasyTrainingField>? previous,
        CancellationToken token)
    {
        var context = round.String("context_name") ?? "";
        if (!IsLive(round)) return [];
        var fields = previous?.Where(field => field.Context == context).ToList() ?? [];
        try
        {
            var isWorlds = round.Object("competition_levels")?.String("code")?.Contains("champ", StringComparison.OrdinalIgnoreCase) == true;
            if (isWorlds)
                await ReadTissotAsync(round, context, riders, fields, token);
            else if (Regex.IsMatch(context, "^[A-Za-z0-9_-]{1,128}$") && !context.StartsWith("planned_", StringComparison.Ordinal))
                await ReadChronoAsync(context, riders, fields, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or Infrastructure.ApiException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Timed-training roster unavailable for Fantasy round {RoundId}; retaining prior evidence or the catalogue pool.", round.String("id"));
        }
        return fields;
    }

    internal static bool IsLive(JsonObject round) => round.String("status")?.ToLowerInvariant() is "live" or "active" or "running";

    private async Task ReadChronoAsync(string context, IReadOnlyList<PinkbikeFantasyRider> riders,
        List<FantasyTrainingField> fields, CancellationToken token)
    {
        var catalog = await chrono.GetCatalogAsync(context, token) as JsonObject;
        var discipline = Children(catalog).FirstOrDefault(node => node.String("Name") == "DHI");
        foreach (var category in Children(discipline))
        {
            var gender = category.String("Name") switch { "ME" or "EM" => "male", "WE" or "EW" => "female", _ => null };
            if (gender is null) continue;
            foreach (var session in Children(category).Where(node => IsTraining(node.String("Name"))))
            {
                var route = Children(session).Select(node => node.String("Route")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (route is null) continue;
                var key = (JsonNode.Parse(route) as JsonObject)?.Object("Params")?.String("key");
                if (key is null) continue;
                var payload = await chrono.GetResultsAsync(context, key, token) as JsonObject;
                // Riders is the published roster. Results alone can be an incomplete training session.
                var names = (payload?.Object("Riders")?.Select(pair => pair.Value).OfType<JsonObject>() ?? [])
                    .Select(rider => $"{rider.String("GivenName")} {rider.String("FamilyName")}").ToArray();
                AddField(gender, context, $"https://prod.chronorace.be/api/results/generic/uci/{Uri.EscapeDataString(context)}/dh/{Uri.EscapeDataString(key)}",
                    names, riders, fields);
            }
        }
    }

    private async Task ReadTissotAsync(JsonObject round, string context, IReadOnlyList<PinkbikeFantasyRider> riders,
        List<FantasyTrainingField> fields, CancellationToken token)
    {
        var year = round.Int("season_year");
        if (year is null) return;
        // Discover event and phase IDs: these are not fixed across seasons.
        var competitions = Objects(await GetAsync($"{TissotRoot}?year={year}", token))
            .Where(item => item.String("sport") == "MTB" && Contains(item.String("name"), "world championship"))
            .Where(item => Contains(item.String("name"), "downhill") || Contains(item.String("code"), "dhi") || item.String("code") == "mtbwch");
        foreach (var competition in competitions)
        {
            var code = competition.String("code");
            if (code is null || !Regex.IsMatch(code, "^[A-Za-z0-9_-]+$")) continue;
            var root = $"{TissotRoot}/{code.ToLowerInvariant()}{year}";
            foreach (var evt in Objects(await GetAsync(root + "/events", token)))
            {
                var name = evt.String("name");
                if (!Contains(name, "elite") || !(Contains(name, "downhill") || Contains(competition.String("name"), "downhill"))) continue;
                var gender = Contains(name, "women") ? "female" : Contains(name, "men") ? "male" : null;
                if (gender is null || evt.Int("number") is not { } eventId) continue;
                var eventRoot = $"{root}/events/{eventId}";
                foreach (var phase in Objects(await GetAsync(eventRoot + "/phases", token)).Where(item => IsTraining(item.String("name"))))
                {
                    if (phase.Int("number") is not { } phaseId) continue;
                    var url = $"{eventRoot}/phases/{phaseId}/startlist";
                    var roster = await GetAsync(url, token) as JsonObject;
                    var names = Objects(roster?["results"]).Select(row => row.Object("rider")?.String("name") ?? "").ToArray();
                    AddField(gender, context, url, names, riders, fields);
                }
            }
        }
    }

    private void AddField(string gender, string context, string source, IReadOnlyList<string> names,
        IReadOnlyList<PinkbikeFantasyRider> riders, List<FantasyTrainingField> fields)
    {
        var ids = MatchRoster(names, riders, gender);
        // An empty/ambiguous/unusable roster must never remove the whole category.
        if (ids.Count < (gender == "male" ? 4 : 2)) return;
        fields.RemoveAll(field => field.Gender == gender);
        fields.Add(new(gender, context, source, time.GetUtcNow(), ids));
    }

    internal static IReadOnlyList<string> MatchRoster(IReadOnlyList<string> names, IReadOnlyList<PinkbikeFantasyRider> riders, string gender)
    {
        var roster = names.Where(name => !string.IsNullOrWhiteSpace(name)).Select(NameKey).ToHashSet(StringComparer.Ordinal);
        return riders.Where(rider => rider.Gender == gender && rider.RiderId is not null)
            .GroupBy(rider => NameKey(rider.Name), StringComparer.Ordinal)
            .Where(group => group.Count() == 1 && roster.Contains(group.Key))
            .Select(group => group.Single().RiderId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static string NameKey(string name) => string.Join('|', Regex.Split(name, @"[\s\-']+")
        .Select(FantasyRiderMapper.NormalizeName).Where(part => part.Length > 0).Order(StringComparer.Ordinal));
    private static bool Contains(string? value, string text) => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
    private static bool IsTraining(string? name) => Contains(name, "timed training") || Contains(name, "timed practice");
    private static IEnumerable<JsonObject> Children(JsonObject? node) => Objects(node?["Childs"]);
    private static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];
    private async Task<JsonNode?> GetAsync(string url, CancellationToken token)
    {
        using var response = await http.GetAsync(url, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
    }
}
