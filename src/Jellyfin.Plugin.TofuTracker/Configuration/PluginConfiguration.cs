using Jellyfin.Plugin.TofuTracker.Core;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TofuTracker.Configuration;

/// <summary>
/// Plugin settings. Jellyfin hands this whole object to any admin API call, so it holds nothing secret:
/// the per-user tokens are in <see cref="LinkStore"/>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the base URL of the TofuTracker scrobbler.</summary>
    public string ServerUrl { get; set; } = ScrobblerUrl.Default;
}
