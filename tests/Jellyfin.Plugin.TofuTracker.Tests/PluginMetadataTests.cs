using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using Jellyfin.Plugin.TofuTracker.Configuration;
using Jellyfin.Plugin.TofuTracker.Core;
using Jellyfin.Plugin.TofuTracker.Playback;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.TofuTracker.Tests;

[Collection(JellyfinStaticsCollection.Name)]
public partial class PluginMetadataTests
{
    [GeneratedRegex(@"^(?<key>\w+):\s*""?(?<value>[^""\r\n]*)""?\s*$", RegexOptions.Multiline)]
    private static partial Regex YamlLine();

    private static Dictionary<string, string> BuildYaml()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "build.yaml"));
        return YamlLine().Matches(text).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);
    }

    [Fact]
    public void Plugin_id_and_name_match_build_yaml()
    {
        var yaml = BuildYaml();

        Assert.Equal(Guid.Parse(yaml["guid"]), Plugin.PluginId);
        Assert.Equal("8ac43e28-3e1b-4d69-bf28-31cbdff1e429", yaml["guid"]);
        Assert.Equal("TofuTracker", yaml["name"]);
        Assert.Equal("12.0.0.0", yaml["targetAbi"]);
        Assert.Equal("net10.0", yaml["framework"]);
    }

    [Fact]
    public void Build_yaml_version_matches_the_assembly_version()
    {
        var assembly = typeof(Plugin).Assembly.GetName().Version;

        Assert.Equal(BuildYaml()["version"], assembly?.ToString());
    }

    [Fact]
    public void Build_yaml_lists_the_plugin_dll_as_an_artifact()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "build.yaml"));

        Assert.Contains("- \"" + typeof(Plugin).Assembly.GetName().Name + ".dll\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Plugin_pages_point_at_embedded_resources_that_exist()
    {
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        var resources = typeof(Plugin).Assembly.GetManifestResourceNames();

        var pages = plugin.GetPages().ToList();

        Assert.Equal(2, pages.Count);
        Assert.Equal("TofuTracker", pages[0].Name);
        Assert.Equal("TofuTracker.js", pages[1].Name);
        Assert.All(pages, p => Assert.Contains(p.EmbeddedResourcePath, resources));
    }

    [Fact]
    public void The_config_page_loads_its_script_by_the_name_the_plugin_serves_it_under()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.TofuTracker.Configuration.configPage.html")!;
        var html = new StreamReader(stream).ReadToEnd();

        Assert.Contains("data-controller=\"__plugin/TofuTracker.js\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_configuration_holds_no_secret_and_round_trips_through_xml()
    {
        var properties = typeof(PluginConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Equal(["ServerUrl"], properties.Select(p => p.Name).ToArray());

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new PluginConfiguration { ServerUrl = "https://example.org" });
        var copy = (PluginConfiguration)serializer.Deserialize(new StringReader(writer.ToString()))!;

        Assert.Equal("https://example.org", copy.ServerUrl);
        Assert.Equal(ScrobblerUrl.Default, new PluginConfiguration().ServerUrl);
    }

    [Fact]
    public async Task The_service_registrator_wires_everything_and_reports_the_server_and_plugin_version()
    {
        using var dir = new TempDir();
        var http = new FakeHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => http.CreateClient());
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(dir.Path);
        paths.PluginsPath.Returns(Path.Combine(dir.Path, "plugins"));
        paths.PluginConfigurationsPath.Returns(Path.Combine(dir.Path, "configurations"));
        Directory.CreateDirectory(paths.PluginConfigurationsPath);
        _ = new Plugin(paths, new TestXmlSerializer()); // sets Plugin.Instance with the default settings
        var host = Substitute.For<IServerApplicationHost>();
        host.ApplicationVersion.Returns(new Version(12, 1, 0));

        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(factory);
        services.AddSingleton(host);
        services.AddSingleton(Substitute.For<ISessionManager>());
        services.AddSingleton(Substitute.For<IUserDataManager>());
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        new PluginServiceRegistrator().RegisterServices(services, host);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<PlaybackEventSource>(Assert.Single(provider.GetServices<IHostedService>()));
        Assert.Same(provider.GetRequiredService<LinkStore>(), provider.GetRequiredService<LinkStore>());

        var userId = Guid.NewGuid();
        provider.GetRequiredService<LinkStore>().Upsert(Samples.Link(userId));
        provider.GetRequiredService<OutboundQueue>().Enqueue(userId, Samples.Event(EventActions.Watched, "s", DateTimeOffset.UtcNow));
        await provider.GetRequiredService<ScrobbleSender>().FlushAsync(default);

        var request = Assert.Single(http.Requests);
        Assert.Equal("https://scrobble.tofutracker.com/v1/events", request.Uri.ToString());
        var client = request.Json.RootElement.GetProperty("client");
        Assert.Equal("jellyfin-plugin", client.GetProperty("name").GetString());
        Assert.Equal("Jellyfin 12.1", client.GetProperty("server").GetString());
        Assert.Equal(typeof(Plugin).Assembly.GetName().Version?.ToString(), client.GetProperty("version").GetString());
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "tofutracker")));
    }
}
