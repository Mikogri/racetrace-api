using System.Net;
using UCI_Stats_API.Configuration;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Infrastructure;

/// <summary>Composition root. Lifetimes, cache policies and option precedence match the existing host.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRaceTraceConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SupabaseOptions>(configuration.GetSection(SupabaseOptions.SectionName));
        services.Configure<ApiAccessOptions>(configuration.GetSection(ApiAccessOptions.SectionName));
        services.Configure<AdminOptions>(configuration.GetSection(AdminOptions.SectionName));
        services.Configure<PointSystemSeedOptions>(configuration.GetSection(PointSystemSeedOptions.SectionName));
        services.PostConfigure<ApiAccessOptions>(options =>
        {
            var environmentSecret = configuration["API_READ_SECRET"];
            if (!string.IsNullOrWhiteSpace(environmentSecret))
            {
                options.ReadSecret = environmentSecret;
            }
        });
        services.PostConfigure<AdminOptions>(options =>
        {
            var environmentSecret = configuration["ADMIN_API_SECRET"];
            if (!string.IsNullOrWhiteSpace(environmentSecret))
            {
                options.ApiSecret = environmentSecret;
            }
        });
        return services;
    }

    public static IServiceCollection AddRaceTraceHttp(this IServiceCollection services)
    {
        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
                options.JsonSerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
            });
        services.AddOpenApi();
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
        });
        services.AddOutputCache(options =>
        {
            options.AddPolicy(
                "ranking",
                new ApiReadOutputCachePolicy(TimeSpan.FromSeconds(30)));
            options.AddPolicy(
                "season-overview",
                new ApiReadOutputCachePolicy(TimeSpan.FromSeconds(60)));
            options.AddPolicy(
                "analytics",
                new ApiReadOutputCachePolicy(TimeSpan.FromMinutes(5)));
        });
        return services;
    }

    public static IServiceCollection AddSupabaseInfrastructure(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services
            .AddHttpClient<ISupabaseRestClient, SupabaseRestClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli
            });
        return services;
    }

    public static IServiceCollection AddRaceTraceApplication(this IServiceCollection services)
    {
        services.AddSingleton<QueryValidationService>();
        services.AddSingleton<RankingSingleFlight>();
        services.AddSingleton<ApiAccessGuard>();
        services.AddSingleton<AdminGuard>();
        services.AddScoped<AdminAuthorizationFilter>();
        services.AddScoped<DomainQueryService>();
        services.AddScoped<PaceService>();
        services.AddScoped<MetricsService>();
        services.AddScoped<SearchService>();
        services.AddScoped<RoundsService>();
        services.AddHttpClient<RoundImageStorageService>();
        services.AddScoped<ResultsService>();
        services.AddScoped<SessionsService>();
        services.AddScoped<RidersService>();
        services.AddScoped<RankingService>();
        services.AddScoped<CompareService>();
        services.AddScoped<RiderSimilarityService>();
        services.AddScoped<AnalyticsService>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<SeasonSummaryService>();
        services.AddScoped<PodiumPredictorService>();
        services.AddScoped<TracksService>();
        services.AddScoped<TeamsService>();
        services.AddScoped<PointsService>();
        services.AddScoped<IPointAwardService, PointAwardService>();
        services.AddScoped<IPointSystemSeeder, PointSystemSeeder>();
        services.AddHostedService<PointSystemSeedHostedService>();
        services.AddScoped<LiveService>();
        services.AddScoped<ConditionsService>();
        services.AddScoped<AdminDataService>();
        services.AddScoped<AdminSessionReimportService>();
        services.AddScoped<UciMtbWorldSeriesSupplementService>();
        services.AddScoped<ChronoRaceImportService>();
        services.AddScoped<RiderProfileImageImportService>();
        services.AddHttpClient<ChronoRaceJsonFetcher>();
        services.AddHttpClient<ChronoRaceLiveProxyService>();
        services.AddHttpClient<TissotTimingJsonFetcher>();
        services.AddScoped<ChronoRaceRoundFormImportService>();
        services.AddSingleton<UciResultsPdfParser>();
        return services;
    }
}
