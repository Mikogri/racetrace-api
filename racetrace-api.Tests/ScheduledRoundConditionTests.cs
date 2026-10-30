using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class ScheduledRoundConditionTests
{
    private static PlannedRoundCreateRequest Round(string status, int? conditionId) => new(
        2027, 1, new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2027, 6, 3, 0, 0, 0, TimeSpan.Zero), status,
        1, "world_cup", "World Cup", 1, 1, null, conditionId, ["ME"]);

    [Fact]
    public void ScheduledRoundAllowsUnknownConditions() => ChronoRaceImportService.ValidatePlannedRound(Round("scheduled", null));

    [Fact]
    public void RoundZeroIsAcceptedButNegativeNumbersAreRejected()
    {
        ChronoRaceImportService.ValidatePlannedRound(Round("scheduled", null) with { RoundNumber = 0 });
        var error = Assert.Throws<ApiException>(() => ChronoRaceImportService.ValidatePlannedRound(Round("scheduled", null) with { RoundNumber = -1 }));
        Assert.Equal("INVALID_ROUND_NUMBER", error.Code);
    }

    [Theory]
    [InlineData("confirmed", null)]
    [InlineData("live", null)]
    [InlineData("cancelled", null)]
    [InlineData("scheduled", 0)]
    public void OtherStatusesStillRequireValidConditions(string status, int? conditionId)
    {
        var error = Assert.Throws<ApiException>(() => ChronoRaceImportService.ValidatePlannedRound(Round(status, conditionId)));
        Assert.Equal("CONDITION_REQUIRED", error.Code);
    }
}
