using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RaceTrace.Api.Infrastructure;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace RaceTrace.Api.Services;

public sealed partial class UciResultsPdfParser
{
    private const double RowTolerance = 1.5;

    public JsonObject Parse(Stream pdfStream, string? fileName = null)
    {
        try
        {
            using var document = PdfDocument.Open(pdfStream);
            var riders = new JsonObject();
            var results = new JsonArray();

            foreach (var page in document.GetPages())
            {
                ParsePage(page, riders, results);
            }

            if (results.Count == 0)
            {
                throw InvalidPdf(
                    $"No result rows with a valid UCI ID were found in the PDF (pages: {document.NumberOfPages})."
                );
            }

            return new JsonObject
            {
                ["ContextName"] = Path.GetFileNameWithoutExtension(fileName ?? "uci-results"),
                ["DisplayName"] = "PDF import",
                ["Riders"] = riders,
                ["Results"] = results,
            };
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw InvalidPdf($"The PDF could not be parsed: {exception.Message}");
        }
    }

    private static void ParsePage(Page page, JsonObject riders, JsonArray results)
    {
        var lines = BuildLines(page);
        foreach (
            var line in lines.Where(candidate =>
                candidate.Words.Any(word =>
                    word.X is >= 205 and < 300 && DigitsOnly(word.Text).Length is >= 8 and <= 12
                )
            )
        )
        {
            var uciId = line
                .Words.Where(word => word.X is >= 205 and < 300)
                .Select(word => DigitsOnly(word.Text))
                .FirstOrDefault(value => value.Length is >= 8 and <= 12);
            if (string.IsNullOrWhiteSpace(uciId))
            {
                continue;
            }

            var raceNumber = FirstInteger(line, 61, 82);
            if (raceNumber is null)
            {
                continue;
            }

            var nameTokens = line
                .Words.Where(word => word.X is >= 76 and < 215)
                .Select(word => NormalizeEmbeddedText(word.Text))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            var (familyName, givenName) = ParseName(nameTokens);
            if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(givenName))
            {
                continue;
            }

            var nation = line
                .Words.Where(word => word.X is >= 270 and < 323)
                .Select(word => word.Text.ToUpperInvariant())
                .FirstOrDefault(value => value.Length == 3 && value.All(char.IsAsciiLetterUpper));
            var yearOfBirth = FirstInteger(line, 300, 350);
            var position = FirstInteger(line, 36, 62);
            var speed = FirstDecimal(line, 330, 402);
            var nextLine = lines
                .Where(candidate => candidate.Y - line.Y is > 4 and < 11)
                .OrderBy(candidate => candidate.Y)
                .FirstOrDefault();

            var split1 = FirstTime(line, 390, 450);
            var split2 = nextLine is null ? null : FirstTime(nextLine, 390, 450);
            var split3 = FirstTime(line, 450, 507);
            var split4 = nextLine is null ? null : FirstTime(nextLine, 450, 507);
            var finish = FirstTime(line, 505, 548);
            var status = ParseStatus(line);
            if (status == "Finished" && finish is null)
            {
                status = ParseStatus(nextLine) is { } nextStatus ? nextStatus : "DNF";
            }

            var technicalKey = raceNumber.Value.ToString(CultureInfo.InvariantCulture);
            riders[technicalKey] = new JsonObject
            {
                ["RaceNr"] = raceNumber,
                ["UciRiderId"] = uciId,
                ["FamilyName"] = familyName,
                ["GivenName"] = givenName,
                ["Nation"] = nation,
                // The importer uses this only when the rider has no exact birth date yet.
                ["YearOfBirth"] = yearOfBirth,
                ["Protected"] = line.Words.Any(word =>
                    word.X is >= 50 and < 65 && NormalizeEmbeddedText(word.Text) == "P"
                ),
            };

            var times = new JsonArray();
            AddSplit(times, 1, split1);
            AddSplit(times, 2, split2);
            AddSplit(times, 3, split3);
            AddSplit(times, 4, split4);
            results.Add(
                new JsonObject
                {
                    ["RaceNr"] = raceNumber,
                    ["Position"] = position,
                    ["RaceTime"] = finish,
                    ["Status"] = status,
                    ["InResult"] = status == "Finished" && position is > 0,
                    ["Speed"] = speed,
                    ["Times"] = times,
                }
            );
        }
    }

    private static List<PdfLine> BuildLines(Page page)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance)
            .Select(word => new PdfWord(
                word.BoundingBox.Left,
                page.Height - word.BoundingBox.Top,
                NormalizeEmbeddedText(word.Text)
            ))
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .GroupBy(word => new
            {
                X = Math.Round(word.X, 0),
                Y = Math.Round(word.Y, 0),
                word.Text,
            })
            .Select(group => group.First())
            .OrderBy(word => word.Y)
            .ThenBy(word => word.X)
            .ToArray();
        var lines = new List<PdfLine>();
        foreach (var word in words)
        {
            var line = lines.LastOrDefault(candidate =>
                Math.Abs(candidate.Y - word.Y) <= RowTolerance
            );
            if (line is null)
            {
                line = new PdfLine(word.Y, []);
                lines.Add(line);
            }

            line.Words.Add(word);
        }

        return lines;
    }

    private static (string? FamilyName, string? GivenName) ParseName(string[] tokens)
    {
        var distinctTokens = tokens
            .Where(
                (token, index) =>
                    index == 0 || !string.Equals(token, tokens[index - 1], StringComparison.Ordinal)
            )
            .ToArray();
        tokens = distinctTokens
            .Where(
                (token, index) =>
                    index + 1 >= distinctTokens.Length
                    || token.Length < distinctTokens[index + 1].Length * 2 - 1
                    || !token.StartsWith(
                        distinctTokens[index + 1],
                        StringComparison.OrdinalIgnoreCase
                    )
            )
            .ToArray();
        if (tokens.Length < 2)
        {
            return (null, null);
        }

        var givenStart = Array.FindIndex(tokens, token => token.Any(char.IsLower));
        if (givenStart <= 0)
        {
            givenStart = tokens.Length - 1;
        }

        return (string.Join(' ', tokens[..givenStart]), string.Join(' ', tokens[givenStart..]));
    }

    private static string ParseStatus(PdfLine? line)
    {
        if (line is null)
        {
            return "Finished";
        }

        var text = string.Join(' ', line.Words.Select(word => word.Text)).ToUpperInvariant();
        foreach (var status in new[] { "DNF", "DNS", "DSQ" })
        {
            if (text.Contains(status, StringComparison.Ordinal))
            {
                return status;
            }
        }

        return "Finished";
    }

    private static void AddSplit(JsonArray times, int index, int? milliseconds)
    {
        if (milliseconds is > 0)
        {
            times.Add(new JsonObject { ["SplitIndex"] = index, ["RaceTime"] = milliseconds });
        }
    }

    private static int? FirstInteger(PdfLine line, double minX, double maxX) =>
        line
            .Words.Where(word => word.X >= minX && word.X < maxX)
            .Select(word => IntegerRegex().Match(word.Text))
            .Where(match => match.Success)
            .Select(match =>
                int.TryParse(match.Value.TrimEnd('.'), CultureInfo.InvariantCulture, out var value)
                    ? value
                    : (int?)null
            )
            .FirstOrDefault(value => value is not null);

    private static decimal? FirstDecimal(PdfLine line, double minX, double maxX) =>
        line
            .Words.Where(word => word.X >= minX && word.X < maxX)
            .Select(word =>
                decimal.TryParse(
                    word.Text.Replace(',', '.'),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var value
                )
                    ? value
                    : (decimal?)null
            )
            .FirstOrDefault(value => value is not null);

    private static string? FirstText(PdfLine line, double minX, double maxX) =>
        line.Words.FirstOrDefault(word => word.X >= minX && word.X < maxX)?.Text;

    private static int? FirstTime(PdfLine line, double minX, double maxX) =>
        line
            .Words.Where(word => word.X >= minX && word.X < maxX)
            .Select(word => UciWorldChampionshipJsonAdapter.ParseTimeMilliseconds(word.Text))
            .FirstOrDefault(value => value is > 0);

    internal static string NormalizeEmbeddedText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var runs = RepeatedCharacterRegex().Matches(value);
        if (
            runs.Count == 0
            || runs.Count(run => run.Length >= 4 && run.Length % 4 == 0)
                < Math.Max(1, runs.Count * 3 / 4)
        )
        {
            return value;
        }

        var output = new StringBuilder();
        foreach (Match run in runs)
        {
            output.Append(run.Value[0], Math.Max(1, run.Length / 4));
        }

        return output.ToString();
    }

    private static string DigitsOnly(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    private static ApiException InvalidPdf(string message) =>
        new(StatusCodes.Status400BadRequest, "INVALID_RESULTS_PDF", message);

    [GeneratedRegex(@"\d+\.?", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerRegex();

    [GeneratedRegex(@"(.)\1*", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedCharacterRegex();

    private sealed record PdfWord(double X, double Y, string Text);

    private sealed record PdfLine(double Y, List<PdfWord> Words);
}
