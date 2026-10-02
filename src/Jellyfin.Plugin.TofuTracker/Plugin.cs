using System.Globalization;
using Jellyfin.Plugin.TofuTracker.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TofuTracker;

/// <summary>The TofuTracker plugin.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin id; it must match <c>guid</c> in build.yaml.</summary>
    public static readonly Guid PluginId = new("8ac43e28-3e1b-4d69-bf28-31cbdff1e429");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "TofuTracker";

    public override Guid Id => PluginId;

    public override string Description => "Sends what you watch in Jellyfin to your TofuTracker library.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", prefix),
        };

        yield return new PluginPageInfo
        {
            Name = Name + ".js",
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.js", prefix),
        };
    }
}
