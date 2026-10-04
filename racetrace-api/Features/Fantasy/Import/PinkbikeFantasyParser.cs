using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RaceTrace.Api.Features.Fantasy.Import;

/// <summary>Reads the public Athletes page's JSON assignments, never executes its JavaScript.</summary>
public static partial class PinkbikeFantasyParser
{
    public const string SourceUrl = "https://www.pinkbike.com/contest/fantasy/dh/athletes/";
    private const int MaximumRiders = 2_000;

    public static IReadOnlyList<PinkbikeFantasyRider> Parse(string html, int season, DateTimeOffset fetchedAtUtc, int minimumRiderCount = 6)
    {
        if (html.Length > 8_000_000)
            throw new InvalidDataException("Pinkbike response is larger than the supported athlete page.");
        using var athletes = ReadAssignment(html, "aAthletesKeyed");
        using var events = ReadAssignment(html, "aAllEventsJS");
        if (athletes.RootElement.ValueKind != JsonValueKind.Object || events.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Pinkbike athlete data schema changed.");

        var completedEvents = events.RootElement.EnumerateArray().Select(item => new
        {
            Round = ReadInteger(item, "round", 1, 100),
            Name = ReadString(item, "name"),
            Date = ReadEventDate(item)
        }).ToArray();
        if (completedEvents.Select(x => x.Round).Distinct().Count() != completedEvents.Length || completedEvents.Any(x => x.Date.Year != season))
            throw new InvalidDataException("Pinkbike history contains duplicate rounds or a different season.");

        var riders = new List<PinkbikeFantasyRider>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in athletes.RootElement.EnumerateObject())
        {
            var data = property.Value;
            var id = ReadString(data, "id");
            if (id != property.Name || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) || numericId <= 0 || !ids.Add(id))
                throw new InvalidDataException("Pinkbike returned invalid or duplicate athlete identifiers.");
            if (ReadInteger(data, "year", 2000, 2200) != season)
                throw new InvalidDataException("Pinkbike athlete prices belong to a different season.");

            var gender = ReadString(data, "gender") switch
            {
                "1" => "male",
                "2" => "female",
                _ => throw new InvalidDataException("Pinkbike returned an unsupported athlete gender.")
            };
            var name = Spaces().Replace($"{ReadString(data, "firstname")} {ReadString(data, "lastname")}".Trim(), " ");
            if (name.Length is < 3 or > 200 || name.Any(char.IsControl))
                throw new InvalidDataException("Pinkbike returned an invalid athlete name.");
            var price = ReadInteger(data, "value", 1, int.MaxValue);
            var points = ReadInteger(data, "totalpoints", 0, int.MaxValue);
            var historyPrices = new Dictionary<int, decimal>();
            foreach (var entry in ReadOptionalString(data, "roundValues").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var round) || round is < 1 or > 100 ||
                    !decimal.TryParse(parts[1], NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var historicalPrice) || historicalPrice < 0 ||
                    !historyPrices.TryAdd(round, historicalPrice))
                    throw new InvalidDataException("Pinkbike returned invalid or duplicate historical prices.");
            }

            var history = completedEvents.Select(round => new PinkbikeHistoricalRound(round.Round, round.Name, round.Date,
                historyPrices.TryGetValue(round.Round, out var historicalPrice) && historicalPrice > 0 ? historicalPrice : null,
                ReadInteger(data, $"round{round.Round}", 0, int.MaxValue))).ToArray();
            riders.Add(new PinkbikeFantasyRider(id, null, name, gender, price, points,
                !string.IsNullOrWhiteSpace(ReadOptionalString(data, "injury")), fetchedAtUtc, "unmapped",
                ReadOptionalString(data, "countryname"), history));
            if (riders.Count > MaximumRiders)
                throw new InvalidDataException("Pinkbike athlete count exceeds the supported limit.");
        }

        if (riders.Count < Math.Max(6, minimumRiderCount) || riders.Count(r => r.Gender == "male") < 4 || riders.Count(r => r.Gender == "female") < 2)
            throw new InvalidDataException("Pinkbike returned an empty or incomplete athlete list.");
        return riders;
    }

    private static JsonDocument ReadAssignment(string html, string variable)
    {
        var declaration = Regex.Match(html, $@"\b(?:let|const|var)\s+{variable}\s*=\s*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!declaration.Success)
            throw new InvalidDataException("Pinkbike athlete data is missing; the page may be blocked or its schema changed.");
        var json = Encoding.UTF8.GetBytes(html[(declaration.Index + declaration.Length)..]);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
        try { return JsonDocument.ParseValue(ref reader); }
        catch (JsonException exception) { throw new InvalidDataException("Pinkbike athlete JSON is invalid.", exception); }
    }

    private static string ReadString(JsonElement data, string key)
    {
        var value = ReadOptionalString(data, key);
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"Pinkbike required field '{key}' is missing.") : value;
    }

    private static string ReadOptionalString(JsonElement data, string key)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Pinkbike athlete or event record is not an object.");
        if (!data.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return "";
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Pinkbike field '{key}' has an unsupported type.");
        return value.GetString() ?? "";
    }

    private static int ReadInteger(JsonElement data, string key, int min, int max) =>
        int.TryParse(ReadString(data, key), NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= min && result <= max
            ? result : throw new InvalidDataException($"Pinkbike field '{key}' is invalid.");

    private static DateOnly ReadEventDate(JsonElement data)
    {
        var value = ReadString(data, "racedate");
        return value.Length >= 10 && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : throw new InvalidDataException("Pinkbike historical event date is invalid.");
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
