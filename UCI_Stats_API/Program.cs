using System.Net;
using UCI_Stats_API.Configuration;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.Configure<SupabaseOptions>(builder.Configuration.GetSection(SupabaseOptions.SectionName));
builder.Services.Configure<ApiAccessOptions>(builder.Configuration.GetSection(ApiAccessOptions.SectionName));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.SectionName));
builder.Services.Configure<PointSystemSeedOptions>(builder.Configuration.GetSection(PointSystemSeedOptions.SectionName));
builder.Services.PostConfigure<ApiAccessOptions>(options =>
{
	var environmentSecret = builder.Configuration["API_READ_SECRET"];
	if (!string.IsNullOrWhiteSpace(environmentSecret))
	{
		options.ReadSecret = environmentSecret;
	}
});
builder.Services.PostConfigure<AdminOptions>(options =>
{
	var environmentSecret = builder.Configuration["ADMIN_API_SECRET"];
	if (!string.IsNullOrWhiteSpace(environmentSecret))
	{
		options.ApiSecret = environmentSecret;
	}
});

builder.Services.AddControllers()
	.AddJsonOptions(options =>
	{
		options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
		options.JsonSerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
	});
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddResponseCompression(options =>
{
	options.EnableForHttps = true;
});
builder.Services.AddOutputCache(options =>
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
builder.Services.AddMemoryCache();
builder.Services
	.AddHttpClient<ISupabaseRestClient, SupabaseRestClient>()
	.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
	{
		AutomaticDecompression = DecompressionMethods.GZip |
			DecompressionMethods.Deflate |
			DecompressionMethods.Brotli
	});

builder.Services.AddSingleton<QueryValidationService>();
builder.Services.AddSingleton<RankingSingleFlight>();
builder.Services.AddSingleton<ApiAccessGuard>();
builder.Services.AddSingleton<AdminGuard>();
builder.Services.AddScoped<AdminAuthorizationFilter>();
builder.Services.AddScoped<DomainQueryService>();
builder.Services.AddScoped<PaceService>();
builder.Services.AddScoped<MetricsService>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<RoundsService>();
builder.Services.AddHttpClient<RoundImageStorageService>();
builder.Services.AddScoped<ResultsService>();
builder.Services.AddScoped<SessionsService>();
builder.Services.AddScoped<RidersService>();
builder.Services.AddScoped<RankingService>();
builder.Services.AddScoped<CompareService>();
builder.Services.AddScoped<RiderSimilarityService>();
builder.Services.AddScoped<AnalyticsService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<SeasonSummaryService>();
builder.Services.AddScoped<PodiumPredictorService>();
builder.Services.AddScoped<TracksService>();
builder.Services.AddScoped<TeamsService>();
builder.Services.AddScoped<PointsService>();
builder.Services.AddScoped<IPointAwardService, PointAwardService>();
builder.Services.AddScoped<IPointSystemSeeder, PointSystemSeeder>();
builder.Services.AddHostedService<PointSystemSeedHostedService>();
builder.Services.AddScoped<LiveService>();
builder.Services.AddScoped<ConditionsService>();
builder.Services.AddScoped<AdminDataService>();
builder.Services.AddScoped<AdminSessionReimportService>();
builder.Services.AddScoped<UciMtbWorldSeriesSupplementService>();
builder.Services.AddScoped<ChronoRaceImportService>();
builder.Services.AddScoped<RiderProfileImageImportService>();
builder.Services.AddHttpClient<ChronoRaceJsonFetcher>();
builder.Services.AddHttpClient<ChronoRaceLiveProxyService>();
builder.Services.AddHttpClient<TissotTimingJsonFetcher>();
builder.Services.AddScoped<ChronoRaceRoundFormImportService>();
builder.Services.AddSingleton<UciResultsPdfParser>();

var app = builder.Build();

app.UseResponseCompression();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseMiddleware<ApiAccessMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
	app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseOutputCache();
app.UseAuthorization();

app.MapControllers();

app.Run();
