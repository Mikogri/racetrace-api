using System.Text.Json.Nodes;
using RaceTrace.Api.PodiumBacktest;

namespace RaceTrace.Api.Tests;

public sealed class IncidentStudyLabelTests
{
    [Fact]
    public void MarkdownReaderKeepsSportingIdentityWithoutSourceNarratives()
    {
        var labels = IncidentDetectorStudy.ReadMarkdown(
            """
            ### Snowshoe #2
            | ID | Data | Zawodnik | Kategoria | Sesja | Typ | Opis | Źródło |
            | 7 | 2021-09-18 | Loïc Bruni | Elite Men | FINAL | `CRASH` | not retained | not retained |
            """,
            2021
        );
        var label = Assert.Single(labels);
        Assert.Equal("Snowshoe #2", label.Event);
        Assert.Equal("EM", label.Category);
        Assert.Equal("Loïc Bruni", label.Rider);
        Assert.DoesNotContain("not retained", label.ToString());
    }

    [Fact]
    public void UnknownSessionsAreNeverGuessedFromAReportedFinalDate()
    {
        Assert.Empty(IncidentDetectorStudy.SessionTypes("Qualifying (Q?)"));
        Assert.Empty(IncidentDetectorStudy.SessionTypes("QUALIFYING / PRACTICE"));
        Assert.Single(IncidentDetectorStudy.SessionTypes("Semi-final"));
    }

    [Fact]
    public void EventMatchingAcceptsThePolishAndProviderSpellingOfBielskoBiala()
    {
        var label = new IncidentStudyLabel(
            "2024-007",
            2024,
            "2. Bielsko-Biała / Szczyrk — 8 zdarzeń",
            "Rider",
            "EM",
            "Final"
        );
        var round = JsonNode
            .Parse(
                """{"tracks":{"venue":"Bielsko-Biala"},"competition_levels":{"code":"world_cup"}}"""
            )!
            .AsObject();
        Assert.True(IncidentDetectorStudy.MatchesEvent(label, round));
    }
}
