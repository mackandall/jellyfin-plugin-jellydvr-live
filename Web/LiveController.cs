using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrLive.Live;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrLive.Web;

/// <summary>
/// For the apps: which buffers exist for a channel (to jump into one), whether an item is a
/// buffer, and Keep. For the settings page: everything, plus current viewers.
/// </summary>
[ApiController]
[Route("JellyDvrLive")]
[Authorize]
public sealed class LiveController : ControllerBase
{
    private readonly BufferManager _manager;

    public LiveController(BufferManager manager)
    {
        _manager = manager;
    }

    /// <summary>Buffers for a channel, oldest first; the last one is the programme on now.</summary>
    [HttpGet("Channel/{channelId}")]
    public IActionResult Channel([FromRoute] Guid channelId) =>
        Ok(new
        {
            Mode = Plugin.Instance?.Configuration.Mode,
            Buffers = _manager.Buffers.Where(b => b.ChannelId == channelId).OrderBy(b => b.StartedUtc).Select(Dto)
        });

    /// <summary>Start buffering a channel now (on-demand mode; harmless in from-tune-in mode).</summary>
    [HttpPost("Channel/{channelId}/Start")]
    public async Task<IActionResult> Start([FromRoute] Guid channelId)
    {
        await _manager.RequestBufferAsync(channelId).ConfigureAwait(false);
        return Channel(channelId);
    }

    /// <summary>
    /// The viewer pressed Stop: stop buffering this channel now to free its tuner input (unless
    /// kept, a real recording, or someone else is watching).
    /// </summary>
    [HttpPost("Channel/{channelId}/Stop")]
    public async Task<IActionResult> Stop([FromRoute] Guid channelId)
    {
        var deviceId = User.FindFirst("Jellyfin-DeviceId")?.Value;
        var stopped = await _manager.StopChannelAsync(channelId, deviceId).ConfigureAwait(false);
        return Ok(new { Stopped = stopped });
    }

    /// <summary>
    /// Guide programmes that are only being recorded as a buffer (not kept, not a real scheduled
    /// recording), so guides can draw their recording mark fainter.
    /// </summary>
    [HttpGet("Programs")]
    public IActionResult Programs() =>
        Ok(new
        {
            ProgramIds = _manager.Buffers
                .Where(b => b.Owned && !b.Kept)
                .Select(b => b.ProgramId.ToString("N"))
                .Distinct()
        });

    /// <summary>The buffer an item belongs to, if any (so a player can offer Keep).</summary>
    [HttpGet("Item/{itemId}")]
    public IActionResult Item([FromRoute] Guid itemId)
    {
        var b = _manager.Buffers.FirstOrDefault(x => x.ItemId == itemId);
        return b is null ? NotFound() : Ok(Dto(b));
    }

    [HttpPost("Keep/{bufferId}")]
    public Task<IActionResult> Keep([FromRoute] string bufferId) => SetKept(bufferId, true);

    [HttpDelete("Keep/{bufferId}")]
    public Task<IActionResult> Unkeep([FromRoute] string bufferId) => SetKept(bufferId, false);

    /// <summary>Everything, for the settings page.</summary>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Status() =>
        Ok(new
        {
            Buffers = _manager.Buffers.OrderByDescending(b => b.StartedUtc).Select(Dto),
            Viewers = _manager.Viewers
        });

    private async Task<IActionResult> SetKept(string bufferId, bool kept)
    {
        var b = await _manager.SetKeptAsync(bufferId, kept).ConfigureAwait(false);
        return b is null ? NotFound() : Ok(Dto(b));
    }

    private static object Dto(LiveBuffer b) => new
    {
        b.Id,
        b.ChannelId,
        b.ChannelName,
        b.ProgramId,
        b.ProgramName,
        ItemId = b.ItemId?.ToString("N"),
        b.StartedUtc,
        b.ProgramEndUtc,
        b.EndedUtc,
        b.LastViewerUtc,
        Recording = b.IsRecording,
        b.Kept,
        b.Owned
    };
}
