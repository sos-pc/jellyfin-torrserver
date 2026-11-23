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
    public string JellyfinInternalUrl { get; set; } = "http://127.0.0.1:8096";
    public string TmdbReadAccessToken { get; set; } = "";
    public string TmdbApiKey { get; set; } = "";
    public string ProwlarrUrl { get; set; } = "";
    public string ProwlarrApiKey { get; set; } = "";
    public string TorrServerUsername { get; set; } = "";
    public string TorrServerPassword { get; set; } = "";
    public int MetadataTimeoutSeconds { get; set; } = 60;
    public int MaxResults { get; set; } = 50;
    public string[] AllowedUserIds { get; set; } = [];
    public string TorrServerUrl { get; set; } = "";
    // « En téléchargement »: Radarr and Sonarr downloads read while qBittorrent fetches them.
    public string QbittorrentUrl { get; set; } = "";
    public string QbittorrentUsername { get; set; } = "";
    public string QbittorrentPassword { get; set; } = "";
    /// <summary>Start of the download paths as qBittorrent sees them, replaced by <see cref="JellyfinPathPrefix"/> when the two containers mount them elsewhere.</summary>
    public string QbittorrentPathPrefix { get; set; } = "";
    public string JellyfinPathPrefix { get; set; } = "";
    public string RadarrUrl { get; set; } = "";
    public string RadarrApiKey { get; set; } = "";
    public string SonarrUrl { get; set; } = "";
    public string SonarrApiKey { get; set; } = "";
    /// <summary>With Seerr, each user sees the downloads they asked for, and administrators see them all. Without it, everyone sees them all.</summary>
    public string SeerrUrl { get; set; } = "";
    public string SeerrApiKey { get; set; } = "";
    public int DownloadStartTimeoutSeconds { get; set; } = 90;
    public string[] DownloadsAllowedUserIds { get; set; } = [];
}
