using RaceTrace.Api.Features.Metrics;
using RaceTrace.Api.Features.Riders;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

using RaceTrace.Api.Services;
using static RaceTrace.Api.Features.Riders.RiderStatisticsProjection;

namespace RaceTrace.Api.Features.Riders;

internal sealed class RiderHistoryQueries(QueryValidationService validator, DomainQueryService domainQueries)
{
    private readonly QueryValidationService _validator = validator;
    private readonly DomainQueryService _domainQueries = domainQueries;

    public async Task<IReadOnlyList<RiderTeamHistoryDto>> GetTeamHistoryAsync(
        string riderId,
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        await EnsureRiderExistsAsync(riderId, cancellationToken);
        var fromDate = _validator.DateAny(
            queryString,
            "dateFrom",
            "date_from",
            "from_date");
        var toDate = _validator.DateAny(
            queryString,
            "dateTo",
            "date_to",
            "to_date");
        _validator.ValidateDateRange(fromDate, toDate);
        var history = await _domainQueries.GetRiderTeamHistoryAsync(
            [riderId],
            fromDate,
            toDate,
            cancellationToken: cancellationToken);

        return history
            .OrderByDescending(row => row.String("valid_from"), StringComparer.Ordinal)
            .Select(row =>
            {
                var team = row.Object("teams");
                var validFrom = PublicDtoMapping.Date(row.String("valid_from")) ??
                    throw new InvalidOperationException("Team history has no valid_from date.");
                var validTo = PublicDtoMapping.Date(row.String("valid_to"));
                return new RiderTeamHistoryDto(
                    PublicDtoMapping.Long(row.String("id")),
                    PublicDtoMapping.Long(row.String("rider_id")),
                    PublicDtoMapping.Long(row.String("team_id")),
                    team?.String("name"),
                    team?.String("code"),
                    validFrom,
                    validTo,
                    validTo is null,
                    PublicDtoMapping.NullableLong(row.String("source_round_id")));
            })
            .ToArray();
    }

    private async Task EnsureRiderExistsAsync(string riderId, CancellationToken cancellationToken)
    {
        if (await _domainQueries.FindByIdAsync("riders", riderId, "id", cancellationToken: cancellationToken) is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "RIDER_NOT_FOUND", "Rider was not found.");
        }
    }

}
