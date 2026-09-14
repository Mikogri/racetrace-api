using UCI_Stats_API.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRaceTraceConfiguration(builder.Configuration)
    .AddRaceTraceHttp()
    .AddSupabaseInfrastructure()
    .AddRaceTraceApplication();

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
