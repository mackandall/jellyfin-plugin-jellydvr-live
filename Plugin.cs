using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.DvrLive.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DvrLive;

/// <summary>
/// JellyDVR Live: a SageTV-style live TV buffer. While someone watches a channel, the programme
/// on it is recorded from the moment they tuned in (a normal Jellyfin recording sharing their
/// tuner stream), so they can rewind past tune-in, carry on from another device, skip
/// commercials, or keep it. Buffers nobody keeps are deleted after a while. Pausing itself
/// stays on the live stream. The dashboard icon comes from meta.json's <c>imagePath</c>.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // This GUID must match meta.json and pluginUniqueId in Configuration/configPage.html.
    private static readonly Guid PluginId = Guid.Parse("cfd76dc2-a07d-4fc9-a2d9-c5d2aaa67215");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "JellyDVR Live";

    public override string Description =>
        "Records the live TV programme you're watching from the moment you tune in, so you can rewind " +
        "past tune-in, switch devices, skip commercials or keep it. Unkept buffers are deleted automatically.";

    public override Guid Id => PluginId;

    /// <summary>Uninstalled from the dashboard: stop and delete its buffers while it still can.</summary>
    public override void OnUninstalling()
    {
        Live.BufferManager.Instance?.ReleaseAllForUninstall();
        base.OnUninstalling();
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}
