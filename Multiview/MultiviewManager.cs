using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrLive.Multiview;

/// <summary>One picture of a server Multiview: the channel and the live stream the server opened for it.</summary>
public sealed class MultiviewTile
{
    public Guid ChannelId { get; init; }

    public string ChannelName { get; init; } = string.Empty;

    public string LiveStreamId { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;
}

/// <summary>A TV's server Multiview: its pictures, its layout, and the ffmpeg that combines them.</summary>
public sealed class MultiviewSession
{
    public Guid Id { get; } = Guid.NewGuid();

    // The stream's address carries this instead of a login (the TV's player sends no headers).
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public Guid UserId { get; init; }

    public string DeviceName { get; set; } = string.Empty;

    public string Layout { get; set; } = MosaicLayout.Grid;

    public List<MultiviewTile> Tiles { get; set; } = new();

    // Bumped on every change: the TV opens the new combined stream, the old one stops.
    public int Generation { get; set; }

    public DateTime CreatedUtc { get; } = DateTime.UtcNow;

    public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;

    internal Process? Ffmpeg { get; set; }

    internal int StreamingGeneration { get; set; } = -1;

    internal readonly SemaphoreSlim Lock = new(1, 1);
}

/// <summary>What the server can do for Multiview, found by a short test run on its GPU.</summary>
public sealed record MultiviewCapability(bool Available, string Reason, string? Device, double? Speed, DateTime CheckedUtc);

/// <summary>
/// Server Multiview: for TVs that can't decode several pictures at once, the server opens the
/// channels' live streams itself, combines them on its GPU (Intel QSV) into one 1080p stream with
/// every picture's sound as its own track, and the TV plays that one stream. The TV keeps the
/// session alive by playing it; a session whose stream nobody has read for a while closes, and its
/// tuners with it, so a TV that crashed or lost its network can't hold them.
/// </summary>
public sealed class MultiviewManager : BackgroundService
{
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CapabilityMemory = TimeSpan.FromHours(12);

    private readonly ILogger<MultiviewManager> _logger;
    private readonly IMediaSourceManager _mediaSources;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IMediaEncoder _encoder;
    private readonly string _heldPath;
    private readonly ConcurrentDictionary<Guid, MultiviewSession> _sessions = new();
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private MultiviewCapability? _capability;

    public MultiviewManager(
        ILogger<MultiviewManager> logger,
        IMediaSourceManager mediaSources,
        ILibraryManager library,
        IUserManager users,
        IMediaEncoder encoder,
        IApplicationPaths paths)
    {
        _heldPath = Path.Combine(paths.DataPath, "jellydvr-live", "multiview.json");
        _logger = logger;
        _mediaSources = mediaSources;
        _library = library;
        _users = users;
        _encoder = encoder;
    }

    /// <summary>Multiview refused: the server is busy with others, or can't do it.</summary>
    public sealed class Refused : Exception
    {
        public Refused(string message) : base(message)
        {
        }
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance!.Configuration;

    public IReadOnlyList<MultiviewSession> Sessions => _sessions.Values.OrderBy(s => s.CreatedUtc).ToList();

    public MultiviewSession? Get(Guid id) => _sessions.TryGetValue(id, out var s) ? s : null;

    private string FfmpegPath()
    {
        if (!string.IsNullOrWhiteSpace(Config.MultiviewFfmpeg))
        {
            return Config.MultiviewFfmpeg;
        }

        // Jellyfin's own build, not a wrapper it may be told to use.
        const string Jellyfins = "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        return File.Exists(Jellyfins) ? Jellyfins : _encoder.EncoderPath;
    }

    // ---- the server's own check ----------------------------------------------------------------

    /// <summary>Whether this server can combine pictures, from a short test run (remembered for 12 h).</summary>
    public async Task<MultiviewCapability> CapabilityAsync(bool again, CancellationToken ct)
    {
        if (!Config.MultiviewEnabled)
        {
            return new MultiviewCapability(false, "Server Multiview is turned off in JellyDVR Live's settings.", null, null, DateTime.UtcNow);
        }

        var known = _capability;
        if (!again && known is not null && DateTime.UtcNow - known.CheckedUtc < CapabilityMemory)
        {
            return known;
        }

        await _checkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var devices = string.IsNullOrWhiteSpace(Config.MultiviewDevice)
                ? Directory.Exists("/dev/dri")
                    ? Directory.GetFiles("/dev/dri", "renderD*").OrderByDescending(d => d, StringComparer.Ordinal).ToList()
                    : new List<string>()
                : new List<string> { Config.MultiviewDevice };
            if (devices.Count == 0)
            {
                return _capability = new MultiviewCapability(false, "No GPU found on the server (/dev/dri).", null, null, DateTime.UtcNow);
            }

            var reason = "The GPU test didn't pass.";
            foreach (var device in devices)
            {
                var (ok, speed, error) = await TestDeviceAsync(device, ct).ConfigureAwait(false);
                if (ok)
                {
                    _logger.LogInformation("Multiview: {Device} combines 4 pictures at {Speed}x real time", device, speed);
                    return _capability = new MultiviewCapability(true, string.Empty, device, speed, DateTime.UtcNow);
                }

                _logger.LogInformation("Multiview: {Device} failed the test: {Error}", device, error);
                reason = $"The GPU test failed on {device}: {error}";
            }

            return _capability = new MultiviewCapability(false, reason, null, null, DateTime.UtcNow);
        }
        finally
        {
            _checkLock.Release();
        }
    }

    // Four generated 1080p pictures through the same GPU path as the real thing, for 5 s.
    private async Task<(bool Ok, double? Speed, string Error)> TestDeviceAsync(string device, CancellationToken ct)
    {
        // The real thing maps VAAPI frames to QSV: the test uploads its pictures to VAAPI too.
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-stats",
            "-init_hw_device", $"vaapi=va:{device}", "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "va"
        };
        for (var i = 0; i < 4; i++)
        {
            args.AddRange(new[] { "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30000/1001" });
        }

        var filter = MosaicLayout.Filter(MosaicLayout.Grid, 4);
        for (var i = 0; i < 4; i++)
        {
            filter = filter.Replace($"[{i}:v:0]hwmap", $"[{i}:v:0]format=nv12,hwupload=extra_hw_frames=16,hwmap", StringComparison.Ordinal);
        }

        args.AddRange(new[] { "-filter_complex", filter, "-map", "[out]", "-t", "5", "-c:v", "h264_qsv", "-preset", "veryfast", "-b:v", "10M", "-f", "null", "-" });
        var psi = Start(args);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg didn't start");
        var reading = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            return (false, null, "it took too long");
        }

        var stderr = await reading.ConfigureAwait(false);

        var speed = Regex.Matches(stderr, @"speed=\s*([0-9.]+)x").LastOrDefault() is { } m
            && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            ? s
            : (double?)null;
        if (p.ExitCode != 0)
        {
            var last = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.Contains("speed=", StringComparison.Ordinal)).LastOrDefault() ?? $"exit code {p.ExitCode}";
            return (false, speed, last.Trim());
        }

        // Slower than real time with nothing else to do: it would stutter.
        return speed is < 1.0 ? (false, speed, $"too slow ({speed}x real time)") : (true, speed, string.Empty);
    }

    private static IEnumerable<string> HwInit(string device) =>
        new[] { "-init_hw_device", $"vaapi=va:{device}", "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "qs" };

    private ProcessStartInfo Start(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(FfmpegPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return psi;
    }

    // ---- sessions ------------------------------------------------------------------------------

    /// <summary>Starts (or, for the same TV, replaces) a Multiview of [channelIds] in [layout].</summary>
    public async Task<MultiviewSession> CreateAsync(Guid userId, string deviceName, IReadOnlyList<Guid> channelIds, string layout, CancellationToken ct)
    {
        Check(channelIds);
        var cap = await CapabilityAsync(false, ct).ConfigureAwait(false);
        if (!cap.Available)
        {
            throw new Refused(cap.Reason);
        }

        // The same TV starting again: its old one goes first (and frees its tuners).
        foreach (var old in _sessions.Values.Where(s => s.UserId == userId && string.Equals(s.DeviceName, deviceName, StringComparison.Ordinal)).ToList())
        {
            await CloseAsync(old.Id).ConfigureAwait(false);
        }

        var others = _sessions.Values.ToList();
        if (others.Count >= Math.Max(1, Config.MultiviewMax))
        {
            var who = string.Join(", ", others.Select(o => o.DeviceName).Where(n => n.Length > 0));
            throw new Refused(who.Length > 0 ? $"Server Multiview is in use on {who}." : "Server Multiview is in use on another TV.");
        }

        var session = new MultiviewSession { UserId = userId, DeviceName = deviceName, Layout = NormalLayout(layout) };
        session.Tiles = await OpenAsync(userId, channelIds, new List<MultiviewTile>(), ct).ConfigureAwait(false);
        _sessions[session.Id] = session;
        WriteHeld();
        _logger.LogInformation("Multiview started for {Device}: {Channels}", deviceName, string.Join(", ", session.Tiles.Select(t => t.ChannelName)));
        return session;
    }

    /// <summary>New pictures or layout: channels still shown keep their streams; the rest close or open.</summary>
    public async Task<MultiviewSession> UpdateAsync(Guid id, Guid userId, IReadOnlyList<Guid> channelIds, string layout, CancellationToken ct)
    {
        Check(channelIds);
        var s = Get(id) ?? throw new KeyNotFoundException();
        if (s.UserId != userId)
        {
            throw new UnauthorizedAccessException();
        }

        await s.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var keep = s.Tiles.Where(t => channelIds.Contains(t.ChannelId)).ToList();
            var gone = s.Tiles.Except(keep).ToList();
            foreach (var t in gone)
            {
                await CloseStreamAsync(t).ConfigureAwait(false);
            }

            s.Tiles = await OpenAsync(userId, channelIds, keep, ct).ConfigureAwait(false);
            s.Layout = NormalLayout(layout);
            s.Generation++;
            s.LastActiveUtc = DateTime.UtcNow;
            StopFfmpeg(s);
            WriteHeld();
            return s;
        }
        finally
        {
            s.Lock.Release();
        }
    }

    public async Task CloseAsync(Guid id)
    {
        if (!_sessions.TryRemove(id, out var s))
        {
            return;
        }

        StopFfmpeg(s);
        foreach (var t in s.Tiles)
        {
            await CloseStreamAsync(t).ConfigureAwait(false);
        }

        WriteHeld();
        _logger.LogInformation("Multiview closed for {Device}", s.DeviceName);
    }

    /// <summary>
    /// The channels each Multiview holds a tuner for, in &lt;data&gt;/jellydvr-live/multiview.json:
    /// JellyDVR Schedule counts them as busy tuners (they have no playback session to see them by).
    /// </summary>
    private void WriteHeld()
    {
        try
        {
            var held = _sessions.Values.Select(s => new
            {
                s.DeviceName,
                ChannelIds = s.Tiles.Select(t => t.ChannelId).ToList(),
                Channels = s.Tiles.Select(t => t.ChannelName).ToList(),
            }).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_heldPath)!);
            var tmp = _heldPath + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(held));
            File.Move(tmp, _heldPath, true);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Couldn't write {Path}", _heldPath);
        }
    }

    private static void Check(IReadOnlyList<Guid> channelIds)
    {
        if (channelIds.Count is < 1 or > 4 || channelIds.Distinct().Count() != channelIds.Count)
        {
            throw new ArgumentException("Multiview takes 1 to 4 different channels.");
        }
    }

    private static string NormalLayout(string? layout) =>
        string.Equals(layout, MosaicLayout.Big, StringComparison.OrdinalIgnoreCase) ? MosaicLayout.Big : MosaicLayout.Grid;

    // The pictures in [channelIds] order: ones in [have] reused, the others opened (all at once).
    private async Task<List<MultiviewTile>> OpenAsync(Guid userId, IReadOnlyList<Guid> channelIds, List<MultiviewTile> have, CancellationToken ct)
    {
        var user = _users.GetUserById(userId) ?? throw new UnauthorizedAccessException();
        var toOpen = channelIds.Where(c => have.All(h => h.ChannelId != c)).ToList();
        var opening = toOpen.Select(c => OpenOneAsync(user, c, ct)).ToList();
        try
        {
            await Task.WhenAll(opening).ConfigureAwait(false);
        }
        catch
        {
            // One failed: close the ones that did open, and say which channel.
            foreach (var t in opening.Where(t => t.IsCompletedSuccessfully))
            {
                await CloseStreamAsync(t.Result).ConfigureAwait(false);
            }

            var failed = opening.Select((t, i) => (t, i)).FirstOrDefault(x => x.t.IsFaulted);
            var name = failed.t is null ? "a channel" : _library.GetItemById(toOpen[failed.i])?.Name ?? "a channel";
            throw new Refused($"The server couldn't open {name}. Its tuner may be busy, or the channel off the air.");
        }

        var opened = opening.Select(t => t.Result).ToList();
        return channelIds.Select(c => have.FirstOrDefault(h => h.ChannelId == c) ?? opened.First(o => o.ChannelId == c)).ToList();
    }

    private async Task<MultiviewTile> OpenOneAsync(Jellyfin.Database.Implementations.Entities.User user, Guid channelId, CancellationToken ct)
    {
        var channel = _library.GetItemById(channelId) ?? throw new KeyNotFoundException($"No channel {channelId}");
        var sources = await _mediaSources.GetPlaybackMediaSources(channel, user, false, false, ct).ConfigureAwait(false);
        var source = sources.FirstOrDefault() ?? throw new InvalidOperationException($"No stream for {channel.Name}");
        if (!source.RequiresOpening)
        {
            return new MultiviewTile { ChannelId = channelId, ChannelName = channel.Name, Path = source.Path };
        }

        var opened = await _mediaSources.OpenLiveStream(
            new LiveStreamRequest
            {
                OpenToken = source.OpenToken,
                UserId = user.Id,
                ItemId = channelId,
                EnableDirectPlay = true,
                EnableDirectStream = true
            },
            ct).ConfigureAwait(false);
        var ms = opened.MediaSource;
        _logger.LogDebug("Multiview opened {Channel}: {Path}", channel.Name, ms.Path);
        return new MultiviewTile { ChannelId = channelId, ChannelName = channel.Name, LiveStreamId = ms.LiveStreamId ?? string.Empty, Path = ms.Path };
    }

    private async Task CloseStreamAsync(MultiviewTile t)
    {
        if (string.IsNullOrEmpty(t.LiveStreamId))
        {
            return;
        }

        try
        {
            await _mediaSources.CloseLiveStream(t.LiveStreamId).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Multiview: closing {Channel}'s stream", t.ChannelName);
        }
    }

    // ---- the combined stream -------------------------------------------------------------------

    /// <summary>
    /// Plays the session's combined stream into [output] until the TV stops reading or the
    /// session changes (then the TV opens the new one). One ffmpeg per session.
    /// </summary>
    public async Task StreamAsync(MultiviewSession s, int generation, Stream output, CancellationToken ct)
    {
        var cap = await CapabilityAsync(false, ct).ConfigureAwait(false);
        if (!cap.Available || cap.Device is null)
        {
            throw new Refused(cap.Reason);
        }

        Process p;
        await s.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (generation != s.Generation)
            {
                throw new KeyNotFoundException();
            }

            StopFfmpeg(s);
            var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
            args.AddRange(HwInit(cap.Device));
            foreach (var t in s.Tiles)
            {
                args.AddRange(new[]
                {
                    "-hwaccel", "vaapi", "-hwaccel_device", "va", "-hwaccel_output_format", "vaapi",
                    "-thread_queue_size", "1024", "-fflags", "+genpts+discardcorrupt",
                    "-analyzeduration", "1000000", "-probesize", "2000000",
                    "-i", t.Path
                });
            }

            args.AddRange(new[] { "-filter_complex", MosaicLayout.Filter(s.Layout, s.Tiles.Count), "-map", "[out]" });
            for (var i = 0; i < s.Tiles.Count; i++)
            {
                args.AddRange(new[] { "-map", $"{i}:a:0?" });
            }

            var rate = Math.Clamp(Config.MultiviewBitrateMbps, 4, 30);
            args.AddRange(new[]
            {
                "-c:v", "h264_qsv", "-preset", "veryfast",
                "-b:v", $"{rate}M", "-maxrate", $"{rate * 14 / 10}M", "-bufsize", $"{rate * 14 / 10}M",
                "-g", "60", "-bf", "0", "-c:a", "copy",
                "-f", "mpegts", "pipe:1"
            });
            p = Process.Start(Start(args)) ?? throw new InvalidOperationException("ffmpeg didn't start");
            s.Ffmpeg = p;
            s.StreamingGeneration = generation;
            _logger.LogInformation("Multiview for {Device}: combining {Count} pictures ({Layout}) on {Gpu}", s.DeviceName, s.Tiles.Count, s.Layout, cap.Device);
        }
        finally
        {
            s.Lock.Release();
        }

        // Its errors drained (a full pipe would stall it), the last few kept for the log.
        var tail = new Queue<string>();
        var errors = Task.Run(
            async () =>
            {
                while (await p.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                {
                    if (tail.Count == 8)
                    {
                        tail.Dequeue();
                    }

                    tail.Enqueue(line);
                }
            },
            CancellationToken.None);
        try
        {
            var buffer = new byte[188 * 512];
            var from = p.StandardOutput.BaseStream;
            int n;
            while ((n = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                s.LastActiveUtc = DateTime.UtcNow;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            // The TV stopped reading (or the session changed): normal.
        }
        finally
        {
            s.LastActiveUtc = DateTime.UtcNow;
            if (ReferenceEquals(s.Ffmpeg, p))
            {
                StopFfmpeg(s);
            }
            else
            {
                Kill(p);
            }

            await errors.ConfigureAwait(false);
            // 137 = killed by us (the pictures changed, or the TV asked again): not a failure.
            if (p.HasExited && p.ExitCode != 0 && p.ExitCode != 137 && tail.Count > 0 && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("Multiview ffmpeg for {Device} ended ({Code}): {Errors}", s.DeviceName, p.ExitCode, string.Join(" | ", tail));
            }

            p.Dispose();
        }
    }

    private static void StopFfmpeg(MultiviewSession s)
    {
        if (s.Ffmpeg is { } p)
        {
            s.Ffmpeg = null;
            Kill(p);
        }
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    // ---- clean-up ------------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WriteHeld(); // none yet: clears what a crash left behind
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                foreach (var s in _sessions.Values.ToList())
                {
                    var streaming = s.Ffmpeg is { HasExited: false };
                    if (!streaming && now - s.LastActiveUtc > IdleClose)
                    {
                        _logger.LogInformation("Multiview for {Device}: nobody watching for {Seconds} s, closing", s.DeviceName, (int)IdleClose.TotalSeconds);
                        await CloseAsync(s.Id).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var s in _sessions.Values.ToList())
            {
                await CloseAsync(s.Id).ConfigureAwait(false);
            }
        }
    }
}
