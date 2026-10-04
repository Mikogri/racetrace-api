using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;

namespace RaceTrace.Api.Services;

public sealed class PointSystemSeedHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWebHostEnvironment _environment;
    private readonly PointSystemSeedOptions _options;
    private readonly ILogger<PointSystemSeedHostedService> _logger;

    public PointSystemSeedHostedService(
        IServiceScopeFactory scopeFactory,
        IWebHostEnvironment environment,
        IOptions<PointSystemSeedOptions> options,
        ILogger<PointSystemSeedHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.FilePath))
        {
            return;
        }

        var path = Path.IsPathRooted(_options.FilePath)
            ? _options.FilePath
            : Path.Combine(_environment.ContentRootPath, _options.FilePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Point system seed file was not found.", path);
        }

        await using var stream = File.OpenRead(path);
        using var scope = _scopeFactory.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<IPointSystemSeeder>();
        var result = await seeder.SeedAsync(stream, cancellationToken);
        _logger.LogInformation(
            "Point system seed completed. Systems: {Systems}, stages: {Stages}, rules: {Rules}.",
            result.SystemsProcessed,
            result.StagesProcessed,
            result.RulesWritten);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
