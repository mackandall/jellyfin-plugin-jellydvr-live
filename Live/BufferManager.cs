using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DvrLive.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrLive.Live;

/// <summary>
/// Keeps a buffer recording going for every channel someone is watching. Watches playback
/// events for live channels; for each watched channel it makes sure the current programme is
/// being recorded (a one-off Live TV timer, which shares the viewer's tuner stream), sets up the
/// next programme's shortly before the boundary, stops buffering a while after the last viewer
/// leaves, tags buffer recordings so other plugins can tell them apart, and deletes buffers
/// nobody kept once they've gone unwatched long enough.
/// </summary>
public sealed class BufferManager : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ViewerTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NextProgrammeLead = TimeSpan.FromSeconds(90);

    private readonly ILogger<BufferManager> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IConfigurationManager _config;
    private readonly IServiceProvider _serviceProvider;
    private readonly BufferStore _store;
    private readonly SemaphoreSlim _tickLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Viewer> _viewers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, DateTime> _requested = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _failedUntil = new();

    // How the last viewer left each channel: true = stopped or changed channel (short grace),
    // false = went silent, e.g. an app closed without saying so (long grace).
    private readonly ConcurrentDictionary<Guid, bool> _leftCleanly = new();
    private CancellationToken _stopping;

    public BufferManager(
        ILogger<BufferManager> logger,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IConfigurationManager config,
        IServiceProvider serviceProvider,
        BufferStore store)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _config = config;
        _serviceProvider = serviceProvider;
        _store = store;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    // Resolved lazily: Live TV services sit in a dependency web that plugins shouldn't pull in
    // at construction time.
    private ILiveTvManager LiveTv => _serviceProvider.GetRequiredService<ILiveTvManager>();

    private IRecordingsManager? Recordings => _serviceProvider.GetService<IRecordingsManager>();

    public IReadOnlyList<LiveBuffer> Buffers => _store.Snapshot();

    public IReadOnlyList<Viewer> Viewers => _viewers.Values.ToList();

    // ---- events ----------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _sessionManager.PlaybackStart += OnPlayback;
        _sessionManager.PlaybackProgress += OnPlayback;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await TickSafeAsync().ConfigureAwait(false);
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _sessionManager.PlaybackStart -= OnPlayback;
            _sessionManager.PlaybackProgress -= OnPlayback;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        }
    }

    private void OnPlayback(object? sender, PlaybackProgressEventArgs e)
    {
        var key = ViewerKey(e.Session?.Id, e.DeviceId, e.PlaySessionId);
        if (key is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        _viewers.TryGetValue(key, out var old);
        if (e.Item is LiveTvChannel channel)
        {
            var isNew = old is null || old.ChannelId != channel.Id;
            if (old is not null && isNew)
            {
                _leftCleanly[old.ChannelId] = true; // changed channel
            }

            _leftCleanly.TryRemove(channel.Id, out _);
            _viewers[key] = new Viewer(channel.Id, channel.Name, e.Session?.UserName ?? string.Empty, e.ClientName ?? string.Empty, now, isNew ? now : old!.TunedInUtc);
            if (isNew)
            {
                // Tuned in (or changed channel): check now (a channel just left may have to give
                // way), and again once they've stayed long enough to buffer. Surfing starts nothing.
                _ = TickAfterAsync(TimeSpan.Zero);
                _ = TickAfterAsync(TimeSpan.FromSeconds(Math.Max(0, Config.StartAfterSeconds) + 1));
            }
        }
        else if (old is not null)
        {
            // Playing something else now (e.g. the buffer itself): no longer on a live channel.
            _leftCleanly[old.ChannelId] = true;
            _viewers.TryRemove(key, out _);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        var key = ViewerKey(e.Session?.Id, e.DeviceId, e.PlaySessionId);
        if (key is not null && e.Item is LiveTvChannel channel && _viewers.TryRemove(key, out _))
        {
            _leftCleanly[channel.Id] = true;
            _ = TickAfterAsync(TimeSpan.Zero);
        }
    }

    private async Task TickAfterAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await TickSafeAsync().ConfigureAwait(false);
    }

    private static string? ViewerKey(string? sessionId, string? deviceId, string? playSessionId) =>
        !string.IsNullOrEmpty(sessionId) ? sessionId : !string.IsNullOrEmpty(deviceId) ? deviceId : playSessionId;

    /// <summary>A viewer asked to start buffering this channel (on-demand mode, or to be sure).</summary>
    public async Task RequestBufferAsync(Guid channelId)
    {
        _requested[channelId] = DateTime.UtcNow;
        await TickSafeAsync().ConfigureAwait(false);
    }

    /// <summary>Keep a buffer: it becomes an ordinary recording (untagged, recorded to the end, never deleted here).</summary>
    public async Task<LiveBuffer?> SetKeptAsync(string bufferId, bool kept)
    {
        LiveBuffer? result = null;
        _store.Update(list =>
        {
            result = list.FirstOrDefault(b => b.Id == bufferId);
            if (result is not null)
            {
                result.Kept = kept;
            }
        });
        if (result is not null)
        {
            await ApplyTagAsync(result).ConfigureAwait(false);
            _logger.LogInformation("{Action} buffer of {Program} on {Channel}", kept ? "Kept" : "Un-kept", result.ProgramName, result.ChannelName);
        }

        return result;
    }

    // ---- the periodic work -----------------------------------------------------------------

    public async Task TickSafeAsync()
    {
        if (!await _tickLock.WaitAsync(0).ConfigureAwait(false))
        {
            return; // a tick is already running; it'll see the same state
        }

        try
        {
            await TickAsync(_stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live buffer check failed");
        }
        finally
        {
            _tickLock.Release();
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var cfg = Config;
        var now = DateTime.UtcNow;

        // Viewers that stopped reporting (app closed without saying so) no longer count.
        foreach (var (key, v) in _viewers)
        {
            if (now - v.LastSeenUtc > ViewerTimeout && _viewers.TryRemove(key, out _))
            {
                _leftCleanly[v.ChannelId] = false; // vanished without a stop
            }
        }

        var watched = _viewers.Values.GroupBy(v => v.ChannelId).ToDictionary(g => g.Key, g => g.First());
        var settled = TimeSpan.FromSeconds(Math.Max(0, cfg.StartAfterSeconds));
        var wanted = cfg.Mode switch
        {
            // Only channels someone has stayed on long enough: surfing past one starts nothing.
            PluginConfiguration.ModeFromTuneIn => _viewers.Values
                .Where(v => now - v.TunedInUtc >= settled)
                .Select(v => v.ChannelId)
                .ToHashSet(),
            PluginConfiguration.ModeOnDemand => _requested.Keys.Where(watched.ContainsKey).ToHashSet(),
            _ => new HashSet<Guid>()
        };
        foreach (var id in _requested.Keys.Where(id => !watched.ContainsKey(id)).ToList())
        {
            _requested.TryRemove(id, out _); // request lapses once nobody watches
        }

        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items;

        RefreshStates(timers, watched, now);

        foreach (var channelId in wanted)
        {
            await EnsureBufferedAsync(channelId, watched[channelId].ChannelName, timers, now, ct).ConfigureAwait(false);
        }

        await GiveWayAsync(cfg, timers, now).ConfigureAwait(false);
        await StopUnwatchedAsync(cfg, now).ConfigureAwait(false);
        await TagAllAsync().ConfigureAwait(false);
        Cleanup(cfg, now);
    }

    // Recording state, file path and library item for each buffer; last-viewer times.
    private void RefreshStates(IReadOnlyList<TimerInfoDto> timers, Dictionary<Guid, Viewer> watched, DateTime now)
    {
        var recordings = Recordings;
        _store.Update(list =>
        {
            foreach (var b in list)
            {
                var timer = b.TimerId is null ? null : timers.FirstOrDefault(t => t.Id == b.TimerId);
                // Jellyfin's schedule is the truth: a timer still New/InProgress means it is (or will
                // be) recording — including one Jellyfin resumed by itself after a server restart.
                var scheduled = timer is not null && (timer.Status == RecordingStatus.New || timer.Status == RecordingStatus.InProgress);
                var activePath = timer is null ? null
                    : recordings?.GetActiveRecordingPath(timer.ExternalId ?? timer.Id) ?? recordings?.GetActiveRecordingPath(timer.Id);
                if (activePath is not null)
                {
                    if (b.Path is null)
                    {
                        b.Path = activePath;
                        if (b.StartedUtc > now)
                        {
                            b.StartedUtc = now; // a next-programme timer that has just begun
                        }
                    }
                    else if (activePath != b.Path && !b.ExtraPaths.Contains(activePath))
                    {
                        b.ExtraPaths.Add(activePath);
                        _logger.LogInformation("Buffer of {Program} on {Channel} continues in {Path}", b.ProgramName, b.ChannelName, activePath);
                    }
                }

                if (b.EndedUtc is not null && scheduled)
                {
                    b.EndedUtc = null;
                    _logger.LogInformation("Jellyfin resumed the buffer of {Program} on {Channel}; managing it again", b.ProgramName, b.ChannelName);
                }

                if (b.IsRecording)
                {
                    var active = activePath is not null || (b.Path is not null && recordings?.GetActiveRecordingInfo(b.Path) is not null);
                    if (!active && !scheduled && now - b.StartedUtc > TimeSpan.FromMinutes(1))
                    {
                        b.EndedUtc = now;
                    }
                }

                if (b.ItemId is null && b.Path is not null)
                {
                    b.ItemId = _libraryManager.FindByPath(b.Path, false)?.Id;
                }

                // The buffer being recorded on a watched channel (or the one just before, while
                // someone is still in it) counts as watched now.
                if (watched.ContainsKey(b.ChannelId) && (b.IsRecording || b.ProgramEndUtc > now.AddMinutes(-1)))
                {
                    b.LastViewerUtc = now;
                }

                if (b.ItemId is { } itemId && _sessionManager.Sessions.Any(s => s.NowPlayingItem?.Id == itemId))
                {
                    b.LastViewerUtc = now; // someone is watching the buffer itself
                }
            }
        });
    }

    // The current programme (and, near the end, the next one) on a watched channel is being recorded.
    private async Task EnsureBufferedAsync(Guid channelId, string channelName, IReadOnlyList<TimerInfoDto> timers, DateTime now, CancellationToken ct)
    {
        var current = ProgrammeAt(channelId, now);
        if (current is null)
        {
            return; // no guide data: nothing to name or bound a recording by
        }

        await TryEnsureProgrammeAsync(channelId, channelName, current, timers, now, ct).ConfigureAwait(false);
        if (current.EndDate is { } end && end - now < NextProgrammeLead && ProgrammeAt(channelId, end.AddSeconds(1)) is { } next)
        {
            await TryEnsureProgrammeAsync(channelId, channelName, next, timers, now, ct).ConfigureAwait(false);
        }
    }

    // A refused timer (tuner conflict, guide changed under us, …) is retried after a pause
    // instead of failing every tick.
    private async Task TryEnsureProgrammeAsync(Guid channelId, string channelName, LiveTvProgram programme, IReadOnlyList<TimerInfoDto> timers, DateTime now, CancellationToken ct)
    {
        if (_failedUntil.TryGetValue(programme.Id, out var until) && now < until)
        {
            return;
        }

        try
        {
            await EnsureProgrammeAsync(channelId, channelName, programme, timers, ct).ConfigureAwait(false);
            _failedUntil.TryRemove(programme.Id, out _);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failedUntil[programme.Id] = now.AddMinutes(5);
            _logger.LogWarning(ex, "Could not start buffering {Program} on {Channel}; will retry in 5 minutes", DisplayName(programme), channelName);
        }
    }

    private async Task EnsureProgrammeAsync(Guid channelId, string channelName, LiveTvProgram programme, IReadOnlyList<TimerInfoDto> timers, CancellationToken ct)
    {
        // Already being recorded (by a buffer still running, or kept). A buffer for this programme
        // that has ended — the viewer left and came back — gets a fresh one for this visit.
        if (_store.Snapshot().Any(b => b.ProgramId == programme.Id && b.ChannelId == channelId && (b.IsRecording || b.Kept)))
        {
            return;
        }

        var programmeId = programme.Id.ToString("N");
        var existing = timers.FirstOrDefault(t => (t.Status == RecordingStatus.New || t.Status == RecordingStatus.InProgress)
            && Guid.TryParse(t.ProgramId, out var pid) && pid == programme.Id);
        var buffer = new LiveBuffer
        {
            ChannelId = channelId,
            ChannelName = channelName,
            ProgramId = programme.Id,
            ProgramName = DisplayName(programme),
            ProgramEndUtc = programme.EndDate ?? DateTime.UtcNow.AddHours(1),
            StartedUtc = programme.StartDate > DateTime.UtcNow ? programme.StartDate : DateTime.UtcNow,
            LastViewerUtc = DateTime.UtcNow
        };

        if (existing is not null)
        {
            // Already being recorded for real: use that recording, never touch it.
            buffer.TimerId = existing.Id;
            buffer.Owned = false;
            if (existing.Status == RecordingStatus.InProgress)
            {
                buffer.StartedUtc = existing.StartDate.AddSeconds(-existing.PrePaddingSeconds);
            }
        }
        else
        {
            var defaults = await LiveTv.GetNewTimerDefaults(programmeId, ct).ConfigureAwait(false);
            await LiveTv.CreateTimer(
                new TimerInfoDto
                {
                    ProgramId = programmeId,
                    ChannelId = defaults.ChannelId,
                    ServiceName = defaults.ServiceName,
                    Name = defaults.Name,
                    StartDate = defaults.StartDate,
                    EndDate = defaults.EndDate,
                    PrePaddingSeconds = 0,
                    PostPaddingSeconds = 0,
                    IsPrePaddingRequired = false,
                    IsPostPaddingRequired = false,
                    KeepUntil = defaults.KeepUntil
                },
                ct).ConfigureAwait(false);
            var created = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items
                .FirstOrDefault(t => Guid.TryParse(t.ProgramId, out var pid) && pid == programme.Id
                    && (t.Status == RecordingStatus.New || t.Status == RecordingStatus.InProgress));
            buffer.TimerId = created?.Id;
        }

        _store.Update(list => list.Add(buffer));
        _logger.LogInformation(
            "Buffering {Program} on {Channel}{Note}",
            buffer.ProgramName,
            channelName,
            buffer.Owned ? string.Empty : " (already being recorded; using that recording)");
    }

    /// <summary>
    /// Give way: when every tuner input is in use (or will be, for a scheduled recording about to
    /// start), stop buffers on channels nobody is watching right away, least recently watched
    /// first, so one input stays free for the next channel someone tunes. Never stops kept buffers,
    /// real recordings, or anything someone is watching.
    /// </summary>
    private async Task GiveWayAsync(PluginConfiguration cfg, IReadOnlyList<TimerInfoDto> timers, DateTime now)
    {
        var limit = cfg.TunerInputs > 0 ? cfg.TunerInputs : JellyfinTunerCount();
        if (limit <= 0)
        {
            return; // unlimited / unknown: nothing to give way for
        }

        var buffers = _store.Snapshot();
        var bufferTimerIds = buffers.Select(b => b.TimerId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var watchedChannels = _viewers.Values.Select(v => v.ChannelId).ToHashSet();
        var playingItems = _sessionManager.Sessions.Select(s => s.NowPlayingItem?.Id).OfType<Guid>().ToHashSet();

        // Channels holding an input now: watched channels, recording buffers, real recordings.
        // Viewers and a buffer on the same channel share one stream.
        var inUse = new HashSet<Guid>(watchedChannels);
        foreach (var t in timers.Where(t => t.Status == RecordingStatus.InProgress))
        {
            inUse.Add(t.ChannelId);
        }

        var dueSoon = timers.Count(t => t.Status == RecordingStatus.New && !bufferTimerIds.Contains(t.Id)
            && t.StartDate.AddSeconds(-t.PrePaddingSeconds) <= now.AddSeconds(90) && !inUse.Contains(t.ChannelId));
        var spare = limit - inUse.Count - dueSoon;
        if (spare >= 1)
        {
            return;
        }

        var candidates = buffers
            .Where(b => b.IsRecording && b.Owned && !b.Kept && b.TimerId is not null
                && !watchedChannels.Contains(b.ChannelId)
                && !(b.ItemId is { } id && playingItems.Contains(id))
                && timers.Any(t => t.Id == b.TimerId && t.Status == RecordingStatus.InProgress))
            .OrderBy(b => b.LastViewerUtc);
        foreach (var b in candidates)
        {
            if (spare >= 1)
            {
                break;
            }

            try
            {
                await LiveTv.CancelTimer(b.TimerId!).ConfigureAwait(false);
                _logger.LogInformation("Stopped buffering {Program} on {Channel} to free a tuner input ({InUse} of {Limit} in use)", b.ProgramName, b.ChannelName, inUse.Count, limit);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                _logger.LogDebug(ex, "Timer {Id} already gone", b.TimerId);
            }

            _store.Update(list =>
            {
                var s = list.FirstOrDefault(x => x.Id == b.Id);
                if (s is not null)
                {
                    s.EndedUtc = now;
                }
            });
            inUse.Remove(b.ChannelId);
            spare++;
        }
    }

    // Jellyfin's configured tuner count across tuners; 0 if any tuner is unlimited.
    private int JellyfinTunerCount()
    {
        var hosts = _config.GetConfiguration<LiveTvOptions>("livetv").TunerHosts ?? Array.Empty<TunerHostInfo>();
        return hosts.Length == 0 || hosts.Any(h => h.TunerCount <= 0) ? 0 : hosts.Sum(h => h.TunerCount);
    }

    // Stop recording buffers on channels nobody has watched for a while (unless kept).
    private async Task StopUnwatchedAsync(PluginConfiguration cfg, DateTime now)
    {
        // Short wait after a viewer stopped or changed channel; the long one only when their app
        // went silent (it may come back, and nothing told us it left).
        var longGrace = TimeSpan.FromMinutes(Math.Max(0, cfg.KeepBufferingMinutes));
        var shortGrace = TimeSpan.FromMinutes(Math.Max(0, cfg.StopAfterLeavingMinutes));
        TimeSpan Grace(LiveBuffer b) => _leftCleanly.TryGetValue(b.ChannelId, out var clean) && clean ? shortGrace : longGrace;
        foreach (var b in _store.Snapshot().Where(b => b.IsRecording && b.Owned && !b.Kept && b.TimerId is not null
            && now - b.LastViewerUtc > Grace(b)))
        {
            try
            {
                await LiveTv.CancelTimer(b.TimerId!).ConfigureAwait(false);
                _logger.LogInformation("Stopped buffering {Program} on {Channel}: no viewers for {Minutes} min", b.ProgramName, b.ChannelName, Grace(b).TotalMinutes);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                _logger.LogDebug(ex, "Timer {Id} already gone", b.TimerId);
            }

            _store.Update(list =>
            {
                var s = list.FirstOrDefault(x => x.Id == b.Id);
                if (s is not null)
                {
                    s.EndedUtc = now;
                }
            });
        }
    }

    private async Task TagAllAsync()
    {
        foreach (var b in _store.Snapshot().Where(b => b.Owned && (b.ItemId is not null || b.ExtraPaths.Count > 0)))
        {
            await ApplyTagAsync(b).ConfigureAwait(false);
        }
    }

    // Unkept buffers carry the tag; kept ones don't. Re-applied every tick, since a metadata
    // refresh can rewrite an item's tags.
    private async Task ApplyTagAsync(LiveBuffer b)
    {
        if (!b.Owned)
        {
            return;
        }

        var tag = Config.BufferTag;
        var items = b.AllPaths.Select(p => _libraryManager.FindByPath(p, false))
            .Append(b.ItemId is { } id ? _libraryManager.GetItemById(id) : null)
            .OfType<BaseItem>()
            .DistinctBy(i => i.Id);
        foreach (var item in items)
        {
            var has = item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
            if (has == !b.Kept)
            {
                continue;
            }

            item.Tags = b.Kept
                ? item.Tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray()
                : item.Tags.Append(tag).ToArray();
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, _stopping).ConfigureAwait(false);
        }
    }

    // Delete buffers nobody kept once they've gone unwatched long enough; forget the rest.
    private void Cleanup(PluginConfiguration cfg, DateTime now)
    {
        var expiry = TimeSpan.FromHours(Math.Max(0, cfg.DeleteAfterHours));
        foreach (var b in _store.Snapshot())
        {
            var gone = b.Path is not null && !b.AllPaths.Any(File.Exists) && !b.IsRecording;
            var expired = !b.IsRecording && now - b.LastViewerUtc > expiry;
            var keptAndDone = b.Kept && !b.IsRecording;
            if (!gone && !expired && !keptAndDone)
            {
                continue;
            }

            if (expired && !gone && b.Owned && !b.Kept && b.Path is not null)
            {
                if (!DeleteBuffer(b))
                {
                    continue;
                }
            }

            _store.Update(list => list.RemoveAll(x => x.Id == b.Id));
        }
    }

    private bool DeleteBuffer(LiveBuffer b)
    {
        var roots = RecordingRoots();
        string? RootOf(string path) => roots.FirstOrDefault(r => path.StartsWith(r + "/", StringComparison.Ordinal));
        try
        {
            foreach (var path in b.AllPaths.ToList())
            {
                if (RootOf(path) is not { } root)
                {
                    _logger.LogWarning("Not deleting buffer file {Path}: not under the recording folders", path);
                    continue;
                }

                var files = RecordingFiles(path);
                if (_libraryManager.FindByPath(path, false) is BaseItem item)
                {
                    _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
                }

                foreach (var f in files.Where(File.Exists))
                {
                    File.Delete(f);
                }

                TidyEmptiedFolders(Path.GetDirectoryName(path)!, root);
                _logger.LogInformation("Deleted unkept buffer {Program} on {Channel} ({Path})", b.ProgramName, b.ChannelName, path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete buffer {Program}; will retry", b.ProgramName);
            return false;
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    private LiveTvProgram? ProgrammeAt(Guid channelId, DateTime time) =>
        _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.TvProgram },
                ChannelIds = new[] { channelId },
                MaxStartDate = time,
                MinEndDate = time,
                Limit = 1
            })
            .OfType<LiveTvProgram>()
            .FirstOrDefault();

    private static string DisplayName(LiveTvProgram p) =>
        string.IsNullOrWhiteSpace(p.EpisodeTitle) ? p.Name : p.Name + " - " + p.EpisodeTitle;

    private List<string> RecordingRoots()
    {
        var tv = _config.GetConfiguration<LiveTvOptions>("livetv");
        return new[] { tv.RecordingPath, tv.MovieRecordingPath, tv.SeriesRecordingPath }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.TrimEnd('/'))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The recording plus its sidecars (.edl, .nfo, -thumb.jpg, Comskip's .txt): same base name + "." or "-".</summary>
    private static List<string> RecordingFiles(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath)!;
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir).Where(f => f == videoPath
                || Path.GetFileName(f).StartsWith(baseName + ".", StringComparison.Ordinal)
                || Path.GetFileName(f).StartsWith(baseName + "-", StringComparison.Ordinal)).ToList()
            : new List<string>();
    }

    // Remove season/show folders the buffer left with no videos (only their .nfo/artwork).
    private void TidyEmptiedFolders(string dir, string root)
    {
        var videoExtensions = new[] { ".ts", ".mkv", ".mp4", ".m4v", ".m2ts", ".mpg", ".mpeg" };
        while (dir.StartsWith(root + "/", StringComparison.Ordinal) && Directory.Exists(dir))
        {
            if (Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Any(f => videoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            {
                return;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not remove emptied folder {Dir}", dir);
                return;
            }

            dir = Path.GetDirectoryName(dir)!;
        }
    }

    public sealed record Viewer(Guid ChannelId, string ChannelName, string UserName, string Client, DateTime LastSeenUtc, DateTime TunedInUtc);
}
