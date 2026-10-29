using RaceTrace.Api.Features.Compare;
using static RaceTrace.Api.Features.Compare.CompareSessionSelection;
using RaceTrace.Api.Features.Metrics;
using System.Globalization;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class CompareService
{
    private const int MaximumComparedRiders = 2;

    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;
    private readonly MetricsService _metrics;
    private readonly RankingService _ranking;
    private readonly CompareInputPackageReader? _inputPackages;

    public CompareService(
        QueryValidationService validator,
        DomainQueryService domainQueries,
        MetricsService metrics,
        RankingService ranking,
        CompareInputPackageReader? inputPackages = null)
    {
        _validator = validator;
        _domainQueries = domainQueries;
        _metrics = metrics;
        _ranking = ranking;
        _inputPackages = inputPackages;
    }

    public async Task<CompareResponseDto> CompareAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var riderIds = ParseRiderIds(queryString, _validator);
        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        if (context.CategoryCodes.Count != 1)
        {
            throw _validator.BadQuery("Select exactly one category for comparison.");
        }
        Task<RiderDetailDataBundle?>? sharedInput = null;
        var inputLock = new object();
        async Task<RiderDetailDataBundle> RiderInputAsync(string riderId, CancellationToken token)
        {
            lock (inputLock)
            {
                sharedInput ??= _inputPackages!.ReadAsync(context, riderIds, token);
            }
            var shared = await sharedInput.WaitAsync(token);
            return shared is null
                ? await _domainQueries.GetRiderDetailDataAsync(context, riderId, cancellationToken: token)
                : CompareInputPackageReader.ForRider(shared, riderId);
        }
        var snapshotTasks = riderIds
            .Select(riderId => _ranking.BuildSnapshotAsync(
                context,
                cancellationToken,
                riderId: riderId,
                suppliedRiderBundleFactory: _inputPackages is null ? null : token => RiderInputAsync(riderId, token)))
            .ToArray();
        var snapshots = await Task.WhenAll(snapshotTasks);
        var sources = new List<CompareRiderSource>(riderIds.Length);

        for (var index = 0; index < riderIds.Length; index++)
        {
            var riderId = riderIds[index];
            var snapshot = snapshots[index];
            var row = snapshot.Rows.FirstOrDefault(item => string.Equals(
                item.String("rider_id"),
                riderId,
                StringComparison.OrdinalIgnoreCase));
            var rider = row?.Object("rider")?.DeepClone().AsObject();
            if (rider is null)
            {
                rider = await _domainQueries.FindByIdAsync(
                    "riders",
                    riderId,
                    cancellationToken: cancellationToken);
                if (rider is null)
                {
                    throw new ApiException(
                        StatusCodes.Status404NotFound,
                        "RIDER_NOT_FOUND",
                        $"Rider '{riderId}' was not found.");
                }
            }

            var source = new CompareRiderSource(riderId, rider, row, snapshot);
            sources.Add(source with
            {
                RoundMetrics = await CalculateRoundMetricsAsync(source, context, cancellationToken)
            });
        }

        var response = BuildResponse(sources);
        ValidateCategoryData(response, context.CategoryCodes[0], _validator);
        return response;
    }

    internal static void ValidateCategoryData(CompareResponseDto response, string category, QueryValidationService validator)
    {
        var expected = PublicApiCodes.ToPublicCategory(category);
        bool Matches(string? value) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
        if (response.Riders.Any(rider => !rider.Categories.Any(Matches)) ||
            response.Sessions.Any(session => session.Category is not null && !Matches(session.Category)))
        {
            throw validator.BadQuery("Both riders must have data in the selected category for the selected range.");
        }
    }

    internal static string[] ParseRiderIds(
        IQueryCollection queryString,
        QueryValidationService validator)
    {
        var values = validator.CsvAny(
            queryString,
            "riderIds",
            "rider_ids",
            "riderId",
            "rider_id",
            "riders");
        var riderIds = values
            .Select(value =>
            {
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                {
                    throw validator.BadQuery("riderIds must contain positive integer identifiers separated by commas.");
                }

                return parsed.ToString(CultureInfo.InvariantCulture);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (riderIds.Length < 2)
        {
            throw validator.BadQuery("riderIds must contain at least two different riders.");
        }

        if (riderIds.Length > MaximumComparedRiders)
        {
            throw validator.BadQuery($"No more than {MaximumComparedRiders} riders can be compared at once.");
        }

        return riderIds;
    }

    internal static CompareResponseDto BuildResponse(IReadOnlyList<CompareRiderSource> sources) =>
        CompareResponseProjection.BuildResponse(sources);

    internal static IReadOnlyList<string> FindCommonSessionIds(IReadOnlyList<CompareRiderSource> sources) =>
        CompareSessionSelection.FindCommonSessionIds(sources);

    internal static IReadOnlyList<CompareSectorGapSummaryDto> SummarizeSectorGaps(IEnumerable<CompareAverageSectorGapDto> sectors) =>
        CompareResponseProjection.SummarizeSectorGaps(sectors);

    private async Task<IReadOnlyDictionary<string, RiderPerformanceMetricValues>> CalculateRoundMetricsAsync(
        CompareRiderSource source,
        RiderMetricsContext context,
        CancellationToken cancellationToken)
    {
        var metrics = new Dictionary<string, RiderPerformanceMetricValues>(StringComparer.OrdinalIgnoreCase);
        var groups = SourceResults(source)
            .GroupBy(ResultRoundId, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key));
        foreach (var group in groups)
        {
            var roundResults = group.ToArray();
            var sessionIds = roundResults
                .Select(ResultSessionId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var availableSessions = source.Snapshot.Sessions
                .Where(session => sessionIds.Contains(session.String("id") ?? string.Empty))
                .ToArray();
            IReadOnlyDictionary<string, RiderPerformanceMetricValues> calculated;
            if (source.Snapshot.PrecomputedSessions is not null)
            {
                var precomputed = source.Snapshot.PrecomputedSessions
                    .Where(session => sessionIds.Contains(session.SessionId))
                    .ToArray();
                calculated = _metrics.CalculatePrecomputedPerformanceByRider(
                    roundResults,
                    precomputed,
                    availableSessions);
            }
            else
            {
                var fieldResults = source.Snapshot.FieldResults
                    .Where(result => sessionIds.Contains(ResultSessionId(result) ?? string.Empty))
                    .ToArray();
                var fieldResultIds = fieldResults
                    .Select(result => result.String("id"))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                calculated = await _metrics.CalculatePerformanceByRiderAsync(
                    roundResults,
                    context,
                    cancellationToken,
                    availableSessions: availableSessions,
                    suppliedSplits: source.Snapshot.Splits
                        .Where(split => fieldResultIds.Contains(split.String("result_id") ?? string.Empty))
                        .ToArray(),
                    suppliedFieldResults: fieldResults);
            }

            if (calculated.TryGetValue(source.RiderId, out var performance))
            {
                metrics[group.Key] = performance;
            }
        }

        return metrics;
    }

}

internal sealed record CompareRiderSource(
    string RiderId,
    JsonObject Rider,
    JsonObject? RankingRow,
    RankingSnapshot Snapshot,
    IReadOnlyDictionary<string, RiderPerformanceMetricValues>? RoundMetrics = null);
