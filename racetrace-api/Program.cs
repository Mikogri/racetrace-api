using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Infrastructure.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRaceTraceTelemetry(builder.Configuration)
    .AddRaceTraceConfiguration(builder.Configuration)
    .AddRaceTraceHttp()
    .AddSupabaseInfrastructure()
    .AddRaceTraceApplication();

builder.Services.Configure<CacheCoherenceOptions>(builder.Configuration.GetSection("CacheCoherence"));

var app = builder.Build();

app.UseResponseCompression();
app.UseMiddleware<ApiTelemetryMiddleware>();
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
