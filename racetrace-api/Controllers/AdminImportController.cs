using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Controllers;

[ApiController]
[Route("api/admin/import")]
[ServiceFilter(typeof(AdminAuthorizationFilter))]
public sealed class AdminImportController : ControllerBase
{
    private readonly ChronoRaceImportService _import;
    private readonly ChronoRaceRoundFormImportService _roundFormImport;
    private readonly RiderProfileImageImportService _profileImages;
    private readonly UciResultsPdfParser _pdfParser;

    public AdminImportController(
        ChronoRaceImportService import,
        ChronoRaceRoundFormImportService roundFormImport,
        RiderProfileImageImportService profileImages,
        UciResultsPdfParser pdfParser)
    {
        _import = import;
        _roundFormImport = roundFormImport;
        _profileImages = profileImages;
        _pdfParser = pdfParser;
    }

    [HttpPost("chronorace/session")]
    [HttpPost("results/session")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.ChronoRaceImportResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ImportChronoRaceSession([FromBody] ChronoRaceImportRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _import.ImportSessionAsync(request, cancellationToken));
    }

    [HttpPost("chronorace/round-form")]
    [HttpPost("results/round-form")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.ChronoRaceRoundFormImportResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ImportChronoRaceRoundForm([FromBody] ChronoRaceRoundFormImportRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _roundFormImport.ImportAsync(request, cancellationToken));
    }

    [HttpPost("results/round-form/pdf")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(50_000_000)]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.ChronoRaceRoundFormImportResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ImportPdfRoundForm(
        [FromForm(Name = "request")] string requestJson,
        [FromForm(Name = "files")] List<IFormFile> files,
        CancellationToken cancellationToken)
    {
        PdfRoundFormImportRequest request;
        try
        {
            request = JsonSerializer.Deserialize<PdfRoundFormImportRequest>(requestJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_PDF_IMPORT_REQUEST", "The multipart request field must contain valid PDF round-form JSON.");
        }

        var fileGroups = files
            .Where(file => file.Length > 0)
            .GroupBy(file => Path.GetFileName(file.FileName), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (fileGroups.Any(group => group.Count() > 1))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "DUPLICATE_PDF_FILE_NAME", "Every uploaded PDF must have a unique file name.");
        }

        var filesByName = fileGroups.ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var sessions = new List<ChronoRaceRoundFormSessionPayload>();
        foreach (var session in request.Sessions ?? [])
        {
            var categories = new List<ChronoRaceRoundFormCategoryPayload>();
            foreach (var category in session.Categories ?? [])
            {
                var fileName = Path.GetFileName(category.PdfFileName);
                if (string.IsNullOrWhiteSpace(fileName) || !filesByName.TryGetValue(fileName, out var file))
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "PDF_FILE_NOT_FOUND", $"PDF file '{category.PdfFileName}' was not included in multipart files.");
                }

                await using var stream = file.OpenReadStream();
                var resultJson = _pdfParser.Parse(stream, fileName);
                categories.Add(new ChronoRaceRoundFormCategoryPayload(category.CategoryCode, null, null, resultJson));
            }

            sessions.Add(new ChronoRaceRoundFormSessionPayload(
                session.Name,
                session.SessionType,
                session.SessionDate,
                session.Status,
                session.Condition,
                categories));
        }

        var normalized = new ChronoRaceRoundFormImportRequest(
            request.Round,
            request.Track,
            request.CompetitionClass,
            request.Condition,
            sessions,
            request.Image);
        return Ok(await _roundFormImport.ImportAsync(normalized, cancellationToken));
    }

    [HttpPost("~/api/admin/rounds")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.PlannedRoundCreateResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreatePlannedRound([FromBody] PlannedRoundCreateRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _import.CreatePlannedRoundAsync(request, cancellationToken));
    }

    [HttpPost("riders/profile-images")]
    [HttpPost("~/api/v1/import/riders/profile-images")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(RaceTrace.Api.Models.RiderProfileImageImportResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ImportRiderProfileImages(
        [FromForm(Name = "files")] List<IFormFile> files,
        CancellationToken cancellationToken)
    {
        return Ok(await _profileImages.ImportAsync(files, cancellationToken));
    }
}
