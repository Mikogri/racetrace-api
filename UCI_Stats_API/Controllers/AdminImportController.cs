using Microsoft.AspNetCore.Mvc;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Controllers;

[ApiController]
[Route("api/admin/import")]
[ServiceFilter(typeof(AdminAuthorizationFilter))]
public sealed class AdminImportController : ControllerBase
{
    private readonly ChronoRaceImportService _import;
    private readonly ChronoRaceRoundFormImportService _roundFormImport;
    private readonly RiderProfileImageImportService _profileImages;

    public AdminImportController(
        ChronoRaceImportService import,
        ChronoRaceRoundFormImportService roundFormImport,
        RiderProfileImageImportService profileImages)
    {
        _import = import;
        _roundFormImport = roundFormImport;
        _profileImages = profileImages;
    }

    [HttpPost("chronorace/session")]
    [HttpPost("results/session")]
    public async Task<IActionResult> ImportChronoRaceSession([FromBody] ChronoRaceImportRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _import.ImportSessionAsync(request, cancellationToken));
    }

    [HttpPost("chronorace/round-form")]
    [HttpPost("results/round-form")]
    public async Task<IActionResult> ImportChronoRaceRoundForm([FromBody] ChronoRaceRoundFormImportRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _roundFormImport.ImportAsync(request, cancellationToken));
    }

    [HttpPost("~/api/admin/rounds")]
    public async Task<IActionResult> CreatePlannedRound([FromBody] PlannedRoundCreateRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _import.CreatePlannedRoundAsync(request, cancellationToken));
    }

    [HttpPost("riders/profile-images")]
    [HttpPost("~/api/v1/import/riders/profile-images")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> ImportRiderProfileImages(
        [FromForm(Name = "files")] List<IFormFile> files,
        CancellationToken cancellationToken)
    {
        return Ok(await _profileImages.ImportAsync(files, cancellationToken));
    }
}
