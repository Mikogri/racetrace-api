using UCI_Stats_API.Models;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class SessionDateTests
{
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
}
