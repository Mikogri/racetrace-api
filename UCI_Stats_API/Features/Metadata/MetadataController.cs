using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace UCI_Stats_API.Features.Metadata;

[ApiController]
[Route("api/v1/metadata")]
public sealed class MetadataController(DisplayMetadataService metadata) : ControllerBase
{
    [HttpGet]
    [OutputCache(PolicyName = "analytics")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await metadata.GetAsync(cancellationToken));
}
