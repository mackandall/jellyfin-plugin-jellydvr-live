using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrLive.Multiview;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrLive.Web;

/// <summary>What a TV asks for: the channels (first = the big picture in "big"), and the layout.</summary>
public sealed class MultiviewRequest
{
    public List<Guid> ChannelIds { get; set; } = new();

    public string Layout { get; set; } = MosaicLayout.Grid;

    public string DeviceName { get; set; } = string.Empty;
}

/// <summary>
/// Server Multiview for the apps: whether the server can do it, start / change / stop one, and the
/// combined stream itself (its address is the session's secret, as the TV's player sends no login).
/// </summary>
[ApiController]
[Route("JellyDvrLive/Multiview")]
[Authorize(Policy = Policies.LiveTvAccess)]
public sealed class MultiviewController : ControllerBase
{
    private readonly MultiviewManager _manager;

    public MultiviewController(MultiviewManager manager)
    {
        _manager = manager;
    }

    private Guid UserId => User.FindFirst("Jellyfin-UserId")?.Value is { } id && Guid.TryParse(id, out var g) ? g : Guid.Empty;

    /// <summary>Can this server combine pictures, and is it free? (?check=true runs the GPU test again.)</summary>
    [HttpGet("Capability")]
    public async Task<IActionResult> Capability([FromQuery] bool check, CancellationToken ct)
    {
        var cap = await _manager.CapabilityAsync(check, ct).ConfigureAwait(false);
        var config = Plugin.Instance!.Configuration;
        return Ok(new
        {
            cap.Available,
            cap.Reason,
            cap.Speed,
            Max = Math.Max(1, config.MultiviewMax),
            InUse = _manager.Sessions.Select(s => s.DeviceName).ToList()
        });
    }

    [HttpPost]
    public async Task<IActionResult> Start([FromBody] MultiviewRequest request, CancellationToken ct)
    {
        try
        {
            var s = await _manager.CreateAsync(UserId, request.DeviceName, request.ChannelIds, request.Layout, ct).ConfigureAwait(false);
            return Ok(Dto(s));
        }
        catch (MultiviewManager.Refused e)
        {
            return Conflict(new { e.Message });
        }
        catch (ArgumentException e)
        {
            return BadRequest(new { e.Message });
        }
    }

    [HttpPost("{id}")]
    public async Task<IActionResult> Change([FromRoute] Guid id, [FromBody] MultiviewRequest request, CancellationToken ct)
    {
        try
        {
            var s = await _manager.UpdateAsync(id, UserId, request.ChannelIds, request.Layout, ct).ConfigureAwait(false);
            return Ok(Dto(s));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (MultiviewManager.Refused e)
        {
            return Conflict(new { e.Message });
        }
        catch (ArgumentException e)
        {
            return BadRequest(new { e.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Stop([FromRoute] Guid id)
    {
        var s = _manager.Get(id);
        if (s is not null && s.UserId == UserId)
        {
            await _manager.CloseAsync(id).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>The combined stream (MPEG-TS, H.264, each picture's sound as its own track, in picture order).</summary>
    [HttpGet("{id}/{token}/{generation}/stream.ts")]
    [AllowAnonymous]
    public async Task Stream([FromRoute] Guid id, [FromRoute] string token, [FromRoute] int generation)
    {
        var ct = HttpContext.RequestAborted;
        var s = _manager.Get(id);
        if (s is null || !string.Equals(s.Token, token, StringComparison.Ordinal) || s.Generation != generation)
        {
            Response.StatusCode = 404;
            return;
        }

        Response.ContentType = "video/mp2t";
        Response.Headers.CacheControl = "no-cache";
        try
        {
            await _manager.StreamAsync(s, generation, Response.Body, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            if (!Response.HasStarted)
            {
                Response.StatusCode = 404;
            }
        }
        catch (MultiviewManager.Refused)
        {
            if (!Response.HasStarted)
            {
                Response.StatusCode = 503;
            }
        }
    }

    private static object Dto(MultiviewSession s)
    {
        var (w, h, tiles) = MosaicLayout.For(s.Layout, s.Tiles.Count);
        return new
        {
            s.Id,
            s.Layout,
            s.Generation,
            StreamPath = $"/JellyDvrLive/Multiview/{s.Id:N}/{s.Token}/{s.Generation}/stream.ts",
            Width = w,
            Height = h,
            Tiles = s.Tiles.Select((t, i) => new
            {
                t.ChannelId,
                t.ChannelName,
                AudioTrack = i,
                tiles[i].X,
                tiles[i].Y,
                tiles[i].W,
                tiles[i].H
            })
        };
    }
}
