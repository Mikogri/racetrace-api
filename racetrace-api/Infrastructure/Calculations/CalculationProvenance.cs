using Microsoft.AspNetCore.Mvc.Filters;

namespace RaceTrace.Api.Infrastructure.Calculations;

public sealed record CalculationMetadata(string AlgorithmVersion, string ConfigurationHash, string? DataRevision, DateTimeOffset ComputedAt);

/// <summary>Request-local metadata; cached computations keep their original timestamp.</summary>
public sealed class CalculationProvenance
{
    public DateTimeOffset? OldestComputedAt { get; private set; }
    public string? ModelVersion { get; set; }
    public DateTimeOffset? PredictionAsOf { get; set; }
    public DateOnly? HistoryCutoff { get; set; }
    public int? SimulationSeed { get; set; }
    public int? SimulationIterations { get; set; }
    public void Include(CalculationMetadata? metadata)
    {
        if (metadata is not null && (OldestComputedAt is null || metadata.ComputedAt < OldestComputedAt))
            OldestComputedAt = metadata.ComputedAt;
    }
}

/// <summary>Checks for concurrent writes before serializing successful analytical responses.
/// Headers are stored together with the original body by OutputCache.</summary>
public sealed class CalculationResponseFilter(DataRevisionService revisions, CalculationProvenance provenance) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var path = http.Request.Path.Value ?? "";
        var analytical = HttpMethods.IsGet(http.Request.Method) &&
            new[] { "/ranking", "/riders", "/compare", "/analytics", "/podium", "/rounds/" }
                .Any(segment => path.Contains(segment, StringComparison.OrdinalIgnoreCase));
        if (!analytical) { await next(); return; }
        var scopes = RequestCalculationIdentity.Scopes(http);
        var observed = revisions.Enabled ? await revisions.GetAsync(http.RequestAborted) : null;
        var started = observed?.ForScopes(scopes);
        var executed = await next();
        if (executed.Exception is not null || http.Response.StatusCode >= 400) return;
        // Ranking snapshots validate before publication. Reusing them must not turn every
        // waiter or memory hit into another physical revision request.
        if (started is not null && started != (await revisions.GetAsync(http.RequestAborted,
            forceRefresh: provenance.OldestComputedAt is null)).ForScopes(scopes))
            throw new ApiException(503, "DATA_REVISION_CHANGED", "Data changed during calculation. Please retry.");
        var algorithm = RequestCalculationIdentity.Algorithm(http);
        http.Response.Headers["X-Algorithm-Version"] = AlgorithmCatalog.Current.VersionLabel(algorithm);
        http.Response.Headers["X-Configuration-Hash"] = AlgorithmCatalog.Current.ConfigurationHash(algorithm);
        if (started is not null) http.Response.Headers["X-Data-Revision"] = started;
        http.Response.Headers["X-Computed-At"] = (provenance.OldestComputedAt ?? DateTimeOffset.UtcNow).ToString("O");
        if (provenance.ModelVersion is not null) http.Response.Headers["X-Model-Version"] = provenance.ModelVersion;
        if (provenance.PredictionAsOf is { } asOf) http.Response.Headers["X-Prediction-As-Of"] = asOf.ToString("O");
        if (provenance.HistoryCutoff is { } cutoff) http.Response.Headers["X-History-Cutoff"] = cutoff.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (provenance.SimulationSeed is { } seed) http.Response.Headers["X-Simulation-Seed"] = seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (provenance.SimulationIterations is { } iterations) http.Response.Headers["X-Simulation-Iterations"] = iterations.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
