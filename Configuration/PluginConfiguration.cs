using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DvrLive.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public const string ModeOff = "off";
    public const string ModeOnDemand = "on-demand";
    public const string ModeFromTuneIn = "from-tune-in";

    /// <summary>
    /// "from-tune-in": buffer every channel someone watches, from the moment they tune in.
    /// "on-demand": only when a viewer asks for it (Start buffer). "off": never.
    /// </summary>
    public string Mode { get; set; } = ModeFromTuneIn;

    /// <summary>Start buffering only once a viewer has stayed on a channel this long (seconds), so surfing starts nothing.</summary>
    public int StartAfterSeconds { get; set; } = 30;

    /// <summary>Stop buffering this long after the last viewer stopped or changed channel (minutes).</summary>
    public int StopAfterLeavingMinutes { get; set; } = 1;

    /// <summary>
    /// Keep buffering this long after the last viewer's app went silent without stopping (minutes),
    /// in case it comes back (network drop, app crash, TV switched off).
    /// </summary>
    public int KeepBufferingMinutes { get; set; } = 10;

    /// <summary>Delete a buffer nobody kept this long after its last viewer left (hours).</summary>
    public int DeleteAfterHours { get; set; } = 4;

    /// <summary>Library tag put on buffer recordings until they're kept (other plugins key off it).</summary>
    public string BufferTag { get; set; } = "Live buffer";
}
