using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TorrServer;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public static Plugin? Instance { get; private set; }
    public override string Name => "TorrServer";
    public override Guid Id => Guid.Parse("655fbf1b-525b-4f82-bdeb-f27dd0bdd606");
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo { Name = Name, EmbeddedResourcePath = "Jellyfin.Plugin.TorrServer.Configuration.configPage.html" }
    };
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string TmdbApiKey { get; set; } = "";
    public string ProwlarrUrl { get; set; } = "";
    public string ProwlarrApiKey { get; set; } = "";
    public string TorrServerUrl { get; set; } = "";
}
