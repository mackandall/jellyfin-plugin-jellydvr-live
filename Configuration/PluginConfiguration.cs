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

    /// <summary>Keep buffering a channel this long after its last viewer leaves (minutes), in case they come back.</summary>
    public int KeepBufferingMinutes { get; set; } = 10;

    /// <summary>Delete a buffer nobody kept this long after its last viewer left (hours).</summary>
    public int DeleteAfterHours { get; set; } = 4;

    /// <summary>Library tag put on buffer recordings until they're kept (other plugins key off it).</summary>
    public string BufferTag { get; set; } = "Live buffer";
}
