using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class SessionDateTests
{
	[Fact]
	public void TissotResolvedUrlIsNotPersistedAsChronoRaceSource()
	{
		Assert.Null(ChronoRaceRoundFormImportService.ResolvePersistedChronoRaceUrl(
			null,
			"https://www.tissottiming.com/2021/mtbdhiwch/event-1/phase-2/results"));
	}

	[Fact]
	public void ResolvedChronoRaceUrlIsPersistedForChronoRaceSource()
	{
		Assert.Equal(
			"https://prod.chronorace.be/api/results/generic/uci/20260619_mtb/dh?key=3",
			ChronoRaceRoundFormImportService.ResolvePersistedChronoRaceUrl(
				"https://prod.chronorace.be/angular/results.html#/uci/event/20260619_mtb/DHI/CG1/dh/3/res",
				"https://prod.chronorace.be/api/results/generic/uci/20260619_mtb/dh?key=3"));
	}

    [Fact]
    public void ImportPayloadWritesSingleSessionDateColumn()
    {
        var payload = new ImportSessionPayload(
            "Q1",
            "Q1",
            new DateOnly(2026, 7, 14),
            "scheduled");

        var values = ChronoRaceImportService.BuildSessionPayload(
            payload,
            "round-1",
            "category-1",
            "condition-1",
            "Q1");

        Assert.Equal("2026-07-14", values["session_date"]!.GetValue<string>());
        Assert.False(values.ContainsKey("start_date"));
        Assert.False(values.ContainsKey("end_date"));
    }

    [Fact]
    public void ImportPayloadPersistsPublicChronoRaceSourceWhenProvided()
    {
        var payload = new ImportSessionPayload(
            "Final",
            "FINAL",
            new DateOnly(2026, 7, 14),
            "live",
            "https://prod.chronorace.be/api/results/generic/uci/round/dh?key=EMF");

        var values = ChronoRaceImportService.BuildSessionPayload(
            payload,
            "round-1",
            "category-1",
            "condition-1",
            "FINAL");

        Assert.Equal(
            "https://prod.chronorace.be/api/results/generic/uci/round/dh?key=EMF",
            values["chronorace_url"]!.GetValue<string>());
    }

    [Fact]
    public void InlineImportDoesNotClearAnExistingChronoRaceSource()
    {
        var values = ChronoRaceImportService.BuildSessionPayload(
            new ImportSessionPayload("Final", "FINAL", null, "live"),
            "round-1",
            "category-1",
            null,
            "FINAL");

        Assert.False(values.ContainsKey("chronorace_url"));
    }
}
