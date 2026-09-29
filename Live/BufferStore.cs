using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrLive.Live;

/// <summary>One buffer: the recording of one programme on one channel, made because someone watched it.</summary>
public sealed class LiveBuffer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public Guid ChannelId { get; set; }

    public string ChannelName { get; set; } = string.Empty;

    public Guid ProgramId { get; set; }

    public string ProgramName { get; set; } = string.Empty;

    /// <summary>The Live TV timer recording it (DTO id, as used by CancelTimer).</summary>
    public string? TimerId { get; set; }

    /// <summary>The recording file, once the recorder has started.</summary>
    public string? Path { get; set; }

    /// <summary>
    /// Further files for the same buffer: after a server restart Jellyfin resumes a recording that
    /// was in progress into a new file ("… - 1.ts"). Tagged, ignored and deleted like <see cref="Path"/>.
    /// </summary>
    public List<string> ExtraPaths { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> AllPaths => (Path is null ? Enumerable.Empty<string>() : new[] { Path }).Concat(ExtraPaths ?? new());

    /// <summary>The library item, once Jellyfin has added it.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>When the recording actually started (position 0 of the recording).</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>When the programme ends (the recording stops then on its own).</summary>
    public DateTime ProgramEndUtc { get; set; }

    /// <summary>Set once the recording has stopped (programme over, or buffering stopped).</summary>
    public DateTime? EndedUtc { get; set; }

    public DateTime LastViewerUtc { get; set; }

    /// <summary>A viewer chose to keep it: now an ordinary recording, never deleted by this plugin.</summary>
    public bool Kept { get; set; }

    /// <summary>
    /// False when the programme was already being recorded by a real (scheduled) timer: that
    /// recording serves as the buffer, but this plugin never stops, tags or deletes it.
    /// </summary>
    public bool Owned { get; set; } = true;

    public bool IsRecording => EndedUtc is null;
}

/// <summary>
/// The buffer list, persisted at a fixed path (&lt;data&gt;/jellydvr-live/buffers.json) so the
/// other JellyDVR plugins can read which recordings are buffers without a code dependency.
/// </summary>
public sealed class BufferStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly ILogger<BufferStore> _logger;
    private readonly string _path;
    private List<LiveBuffer> _buffers;

    public BufferStore(IApplicationPaths paths, ILogger<BufferStore> logger)
    {
        _logger = logger;
        _path = System.IO.Path.Combine(paths.DataPath, "jellydvr-live", "buffers.json");
        _buffers = Load();
    }

    public List<LiveBuffer> Snapshot()
    {
        lock (_lock)
        {
            return _buffers.ToList();
        }
    }

    /// <summary>Applies a change under the lock and saves.</summary>
    public void Update(Action<List<LiveBuffer>> change)
    {
        lock (_lock)
        {
            change(_buffers);
            Save();
        }
    }

    private List<LiveBuffer> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<List<LiveBuffer>>(File.ReadAllText(_path)) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read {Path}; starting with no buffers", _path);
        }

        return new();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_buffers, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save {Path}", _path);
        }
    }
}
