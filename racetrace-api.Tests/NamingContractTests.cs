using Microsoft.AspNetCore.Mvc;
using RaceTrace.Api.Controllers;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Tests;

public sealed class NamingContractTests
{
    [Fact]
    public void RankingControllerUsesRankingRoute()
    {
        var route = Assert.Single(
            typeof(RankingController)
                .GetCustomAttributes(typeof(RouteAttribute), inherit: true)
                .Cast<RouteAttribute>()
        );

        Assert.Equal("api/v1/ranking", route.Template);
    }

    [Fact]
    public void CompareControllerUsesCompareRoute()
    {
        var route = Assert.Single(
            typeof(CompareController)
                .GetCustomAttributes(typeof(RouteAttribute), inherit: true)
                .Cast<RouteAttribute>()
        );

        Assert.Equal("api/v1/compare", route.Template);
    }

    [Fact]
    public void CompetitionClassesEndpointUsesClassTerminology()
    {
        var action = typeof(PointsController).GetMethod(
            nameof(PointsController.CompetitionClasses)
        );
        var route = Assert.Single(
            action!
                .GetCustomAttributes(typeof(HttpGetAttribute), inherit: true)
                .Cast<HttpGetAttribute>()
        );

        Assert.Equal("api/v1/competition-classes", route.Template);
    }

    [Fact]
    public void PublicModelsUseCompetitionClassTerminology()
    {
        Assert.NotNull(typeof(ChronoRaceImportRequest).GetProperty("CompetitionClass"));
        Assert.Null(typeof(ChronoRaceImportRequest).GetProperty("CompetitionRank"));
        Assert.NotNull(typeof(RoundListItemDto).GetProperty("CompetitionClassId"));
        Assert.Null(typeof(RoundListItemDto).GetProperty("CompetitionLevelId"));
    }
}
