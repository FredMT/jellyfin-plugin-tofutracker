using Jellyfin.Plugin.TofuTracker.Core;
using Jellyfin.Plugin.TofuTracker.Playback;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TofuTracker;

/// <summary>Registers the plugin's services with the server.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        ArgumentNullException.ThrowIfNull(applicationHost);

        serviceCollection.TryAddSingleton(TimeProvider.System);

        serviceCollection.AddSingleton(sp => new LinkStore(DataDirectory(sp), Logger(sp)));
        serviceCollection.AddSingleton(sp => new OutboundQueue(DataDirectory(sp), Logger(sp), sp.GetRequiredService<TimeProvider>()));
        serviceCollection.AddSingleton(sp => new PlaybackPlanner(sp.GetRequiredService<TimeProvider>()));

        serviceCollection.AddSingleton(sp => new ScrobbleSender(
            sp.GetRequiredService<OutboundQueue>(),
            sp.GetRequiredService<LinkStore>(),
            HttpClientSource(sp),
            ConfiguredServerUrl,
            () => ClientDescription(applicationHost),
            sp.GetRequiredService<TimeProvider>(),
            Logger(sp)));

        serviceCollection.AddSingleton(sp => new PairingCoordinator(
            HttpClientSource(sp),
            ConfiguredServerUrl,
            () => ClientDescription(applicationHost),
            sp.GetRequiredService<LinkStore>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger(sp)));

        serviceCollection.AddHostedService<PlaybackEventSource>();
    }

    private static string ConfiguredServerUrl() => ScrobblerUrl.NormalizeOrDefault(Plugin.Instance?.Configuration.ServerUrl);

    private static string DataDirectory(IServiceProvider services)
    {
        return Path.Combine(services.GetRequiredService<IApplicationPaths>().DataPath, "tofutracker");
    }

    private static ILogger Logger(IServiceProvider services)
    {
        return services.GetRequiredService<ILoggerFactory>().CreateLogger("Jellyfin.Plugin.TofuTracker");
    }

    /// <summary>The server's shared "Default" client, which already carries the server's proxy and network settings.</summary>
    private static Func<HttpClient> HttpClientSource(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IHttpClientFactory>();
        return () => factory.CreateClient(NamedClient.Default);
    }

    private static ClientInfo ClientDescription(IServerApplicationHost host)
    {
        var plugin = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        var server = host.ApplicationVersion;
        return new ClientInfo("jellyfin-plugin", plugin, $"Jellyfin {server.Major}.{server.Minor}");
    }
}
