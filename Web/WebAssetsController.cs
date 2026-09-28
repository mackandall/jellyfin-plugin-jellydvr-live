using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrLive.Web;

/// <summary>
/// Serves the web-player script (live.js) embedded in this assembly. jellyfin-web's
/// index.html is patched to load it from here (install-web.sh; the file is root-owned), so a
/// plugin redeploy updates the script without touching the web root.
///
/// Anonymous: index.html loads it on every page, including the login screen. The script does
/// nothing until this browser's session is playing something.
/// </summary>
[ApiController]
[Route("JellyDvrLive/web")]
[AllowAnonymous]
public sealed class WebAssetsController : ControllerBase
{
    [HttpGet("live.js")]
    [Produces("application/javascript")]
    public async Task<ContentResult> GetLiveJs()
    {
        var assembly = typeof(WebAssetsController).Assembly;
        var resourceName = $"{assembly.GetName().Name}.Web.live.js";
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return Content(await reader.ReadToEndAsync().ConfigureAwait(false), "application/javascript");
    }
}
