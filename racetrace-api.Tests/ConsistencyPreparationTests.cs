using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class ConsistencyPreparationTests
{
    [Fact]
    public void OutlierProfileKeepsInterpolatedReferenceAndCleanMedian()
    {
        var sectors = new[] { .8, .9, .1, 1.0 }
            .Select(
                (percentile, index) =>
                    new PaceSectorResult(
                        index,
                        PaceSectorStatus.Valid,
                        30,
                        null,
                        percentile,
                        null,
                        null
                    )
            )
            .ToArray();
        var result = new DownhillConsistencyCalculator().AnalyzeSectors(sectors);

        Assert.Equal(2, Assert.Single(result.SectorIndexes));
        Assert.Equal(90, result.ProjectedFinishPositionScore);
    }

    [Fact]
    public void SectorLookupKeepsFirstDuplicateAndOriginalKeyCasing()
    {
        var result = new DownhillConsistencyCalculator().Calculate([
            new("a", "Course", PaceSessionType.Final, [new("S1", 30), new("s1", 300)]),
            new("b", "course", PaceSessionType.Final, [new("s1", 30)]),
        ]);

        var sector = Assert.Single(result.Sectors);
        Assert.Equal("Course", sector.CourseKey);
        Assert.Equal("S1", sector.SectorKey);
        Assert.Equal(100, sector.Consistency);
        Assert.Equal(0, sector.RelativeVariation);
        Assert.Equal(2, sector.CompletedRuns);
    }

    [Fact]
    public void InvalidFirstDuplicateIsExcludedButMissingSectorCountsAsAnAttempt()
    {
        var result = new DownhillConsistencyCalculator().Calculate([
            new(
                "a",
                "course",
                PaceSessionType.Final,
                [new("S1", 30, IsInvalidTiming: true), new("s1", 30)]
            ),
            new("b", "course", PaceSessionType.Final, [new("S1", 30)]),
            new("c", "course", PaceSessionType.Final, []),
        ]);

        var sector = Assert.Single(result.Sectors);
        Assert.Equal(2, sector.TotalRuns);
        Assert.Equal(1, sector.CompletedRuns);
        Assert.Null(sector.RelativeVariation);
        Assert.Equal(0, sector.Consistency);
    }
}
