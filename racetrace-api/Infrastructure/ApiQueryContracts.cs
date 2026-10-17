using Microsoft.OpenApi;
using System.Text.Json.Nodes;

namespace RaceTrace.Api.Infrastructure;

/// <summary>Metadata for legacy actions that read Request.Query rather than bound DTOs.</summary>
internal static class ApiQueryContracts
{
    private const string Metrics = "season,seasons,seasonYears,season_years,seasonYear,season_year,trackId,track_id,tracks,venueId,venue_id,venue,venueName,venue_name,countryCode,country_code,category,categories,categoryCode,categoryCodes,category_code,category_codes,sessionType,session_type,dateFrom,date_from,from_date,dateTo,date_to,to_date,competitionClassId,competition_class_id,competitionClassCode,competition_class_code,class,conditionId,condition_id,conditions,condition,roundId,round_id,status,trackName,track_name";
    private const string Paging = "page,pageSize,page_size,limit,sortBy,sort_by,sort,sortDirection,sort_direction,order";
    private const string Ranking = "search,q,nation,comparison,comparisonSeason,comparisonTrackId,comparisonConditionId,comparisonCompetitionClassId,comparisonCategory,minRating,min_rating,maxRating,max_rating,minPace,min_pace,maxPace,max_pace,minConsistency,min_consistency,maxConsistency,max_consistency,confidence,minConfidence,min_confidence,maxConfidence,max_confidence,minPoints,min_points,maxPoints,max_points,minStarts,min_starts,maxStarts,max_starts,minFinalStarts,min_final_starts,maxFinalStarts,max_final_starts";
    private const string Results = "category,categoryCode,category_code,sessionType,session_type,search,q,countryCode,country_code,nation,status,min_race_time_ms,max_race_time_ms,min_top_speed,max_top_speed,min_gap_ms,max_gap_ms,timeView,time_view,timing_mode";

    public static void Add(OpenApiOperation operation, string controller, string action)
    {
        var key = $"{controller}.{action}";
        var groups = key switch
        {
            "Ranking.List" or "Riders.List" => new[] { Metrics, Paging, Ranking },
            "Riders.Metrics" or "Riders.Overview" => [Metrics],
            "Riders.Results" or "Riders.Venues" => [Metrics, Paging],
            "Riders.Get" => [Metrics, "include"],
            "Riders.Resolve" => ["uciIds"],
            "Riders.TeamHistory" => ["dateFrom,date_from,from_date,dateTo,date_to,to_date"],
            "Analytics.RoundProfile" or "Analytics.PressurePerformance" => [Metrics],
            "Compare.Get" => [Metrics, "riderIds,rider_ids,riderId,rider_id,riders"],
            "Sessions.Common" => [Metrics, Paging, "riderId,rider_id,commonSessionsOnly,common_sessions_only"],
            "Rounds.List" => [Paging, "season,seasonYear,season_year,search,q,competitionClassCode,competition_class_code,competitionClassId,competition_class_id,countryCode,country_code,venueName,venue,trackName,track_name,status,category,categoryCode,category_code"],
            "Rounds.Get" or "Results.Get" or "Live.Session" => ["include"],
            "Rounds.Sessions" => [Paging, "category,categoryCode,category_code,status,sessionType,session_type,type,search,q"],
            "Rounds.Results" or "Results.SessionResults" or "Live.Leaderboard" => [Paging, Results],
            "Results.TimeSplits" => ["sortBy,sort_by,sort,sortDirection,sort_direction,order"],
            "Live.Sessions" => [Metrics, "sortBy,sort_by,sort,sortDirection,sort_direction,order"],
            "PodiumPredictor.Get" => ["season,seasonYear,season_year,category,categoryCode,category_code,roundId"],
            "Tracks.List" => [Paging, "countryCode,country_code,venueName,venue,trackName,name,min_length_m,max_length_m,min_avg_slope,max_avg_slope,search,q"],
            "Tracks.Get" => [Metrics, "include"],
            "Tracks.Records" => [Metrics, Paging],
            "Teams.List" => [Paging, "code,q"],
            "Conditions.List" => [Paging, "is_dry,q"],
            "Points.Systems" => ["season_year,discipline,category_code,competition_class_code,session_type"],
            "Points.CompetitionClasses" => ["is_active"],
            "Search.Search" => ["q,type,limit"],
            _ => []
        };
        operation.Parameters ??= [];
        foreach (var name in groups.SelectMany(group => group.Split(',')).Distinct(StringComparer.Ordinal))
        {
            if (operation.Parameters.Any(parameter => parameter.Name == name)) continue;
            var integer = name is "page" or "pageSize" or "page_size" or "limit" or "minSectors" or "min_sectors" or "minSessions" or "min_sessions" or "minSharedFeatures" or "min_shared_features" or "clusterCount" or "cluster_count" or "min_length_m" or "max_length_m" or "min_race_time_ms" or "max_race_time_ms" or "min_gap_ms" or "max_gap_ms" || name.Contains("Starts", StringComparison.Ordinal) || name.EndsWith("_starts", StringComparison.Ordinal);
            var boolean = name is "is_dry" or "is_active" or "commonSessionsOnly" or "common_sessions_only";
            var date = name is "dateFrom" or "date_from" or "from_date" or "dateTo" or "date_to" or "to_date";
            var number = name.StartsWith("min", StringComparison.Ordinal) || name.StartsWith("max", StringComparison.Ordinal) || name is "confidence";
            var schema = new OpenApiSchema
            {
                Type = boolean ? JsonSchemaType.Boolean : integer ? JsonSchemaType.Integer : number ? JsonSchemaType.Number : JsonSchemaType.String,
                Format = date ? "date" : integer ? "int32" : null
            };
            if (name is "sortDirection" or "sort_direction" or "order") schema.Enum = [JsonValue.Create("asc")!, JsonValue.Create("desc")!];
            if (name is "timeView" or "time_view" or "timing_mode") schema.Enum = [JsonValue.Create("sectors")!, JsonValue.Create("splits")!];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = name, In = ParameterLocation.Query, Required = false, Schema = schema,
                Description = "Existing query parameter/alias; CSV filters remain comma-separated strings. Validation and defaults are owned by the backend."
            });
        }
    }
}
