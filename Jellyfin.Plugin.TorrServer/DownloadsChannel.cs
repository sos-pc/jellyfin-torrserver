using System.Globalization;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Drawing;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>
/// « En téléchargement »: what Radarr and Sonarr are downloading, playable before the download ends. Nothing changes for
/// the trackers: qBittorrent downloads the whole torrent and seeds it as usual; Jellyfin reads the file as it arrives.
/// </summary>
public sealed class DownloadsChannel : IChannel, IRequiresMediaInfoCallback, IHasCacheKey
{
    private readonly DownloadCatalog _catalog;
    private readonly DownloadTickets _tickets;
    private readonly IMediaSourceManager _media;
    private readonly IUserManager _users;
    private readonly Func<PluginConfiguration> _config;

    public DownloadsChannel(DownloadCatalog catalog, DownloadTickets tickets, IMediaSourceManager media, IUserManager users, Func<PluginConfiguration> config)
    { _catalog = catalog; _tickets = tickets; _media = media; _users = users; _config = config; }

    public string Name => "En téléchargement";
    public string Description => "Les demandes que Radarr et Sonarr téléchargent, à regarder sans attendre la fin du téléchargement.";
    public string DataVersion => "1";
    public string HomePageUrl => "";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    /// <summary>Progress changes all the time: listings are kept 30 seconds, per user since each one sees their own requests.</summary>
    public string? GetCacheKey(string? userId) => (userId ?? "") + "-" + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30).ToString(CultureInfo.InvariantCulture);
    public bool IsEnabledFor(string userId) => _catalog.Qbit.Configured && (_config().DownloadsAllowedUserIds.Length == 0 || _config().DownloadsAllowedUserIds.Any(id => DownloadCatalog.NormalizeId(id) == DownloadCatalog.NormalizeId(userId)));
    public InternalChannelFeatures GetChannelFeatures() => new() { MediaTypes = [ChannelMediaType.Video], ContentTypes = [ChannelMediaContentType.Movie, ChannelMediaContentType.Episode], AutoRefreshLevels = 0 };
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) => Task.FromResult(new DynamicImageResponse { HasImage = type == ImageType.Primary, Stream = typeof(DownloadsChannel).Assembly.GetManifestResourceStream("Jellyfin.Plugin.TorrServer.logo.png"), Format = ImageFormat.Png });

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        var userId = query.UserId.ToString("N");
        var downloads = await _catalog.List(userId, SeesAll(query.UserId), ct);
        var id = query.FolderId ?? "";
        var items = new List<ChannelItemInfo>();
        if (id == "")
        {
            // Jellyfin never renames a video of a channel once created, only a folder: each download is a folder whose
            // name carries its live progress, and its videos keep stable names (and their watch state).
            foreach (var (entry, torrent) in downloads)
                items.Add(new ChannelItemInfo { Id = $"torrent:{entry.Hash}", Name = FolderName(entry, torrent), Overview = entry.Release, ImageUrl = entry.Poster, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container });
            return Result(items);
        }
        var parts = id.Split(':');
        if (parts.Length != 2 || parts[0] != "torrent") throw new ArgumentException("Dossier invalide.");
        var (owner, _) = downloads.FirstOrDefault(x => x.Entry.Hash == QbitClient.NormalizeHash(parts[1]));
        if (owner is null) return Result(items);
        var videos = (await _catalog.Qbit.Files(owner.Hash, ct)).Where(f => f.Priority > 0 && Services.IsVideo(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var file in videos)
            items.Add(Video(owner, $"file:{owner.Hash}:{file.Index}", owner.IsMovie && videos.Count == 1 ? owner.Title : FileLabel(owner, file)));
        return Result(items);
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        var parts = id.Split(':');
        if (parts.Length != 3 || parts[0] != "file" || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var index)) throw new ArgumentException("Fichier invalide.");
        var qbit = _catalog.Qbit;
        var file = await qbit.Locate(parts[1], index, ct);
        // A complete file needs nothing from qBittorrent; one still arriving is brought first.
        if (file.File.Progress < 1) await qbit.Prioritize(file.Hash, index, ct);
        // The start of the file must be there before Jellyfin probes it; its end only for an MP4 that keeps its index there.
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_config().DownloadStartTimeoutSeconds, 1, 600)));
            var size = file.File.Size;
            var head = Containers.HeadSize(size);
            try
            {
                await qbit.WaitForBytes(file, 0, head - 1, wait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("Le début du fichier n'est pas encore téléchargé. Réessayez dans une minute.");
            }
            if (Containers.NeedsTail(file.File.Name, await Containers.ReadStart(file.Path, (int)Math.Min(head, 1024 * 1024), ct)))
            {
                try
                {
                    await qbit.WaitForBytes(file, Math.Max(0, size - head), size - 1, wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Ce MP4 range son index à la fin du fichier, qui n'est pas encore téléchargée. Réessayez dans une minute.");
                }
            }
        }
        var source = new MediaSourceInfo
        {
            Id = Probe.SourceId(id),
            Name = Path.GetFileName(file.File.Name),
            Path = Services.Endpoint(_config().JellyfinInternalUrl, "TorrServerPlugin/download/" + _tickets.Issue(file.Hash, index)).AbsoluteUri,
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            Container = Path.GetExtension(file.File.Name).TrimStart('.').ToLowerInvariant(),
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            Size = file.File.Size,
        };
        await Probe.Run(_media, source, ct);
        return [source];
    }

    /// <summary>Administrators see every download; the others, what they asked for in Seerr.</summary>
    private bool SeesAll(Guid userId)
    {
        if (string.IsNullOrWhiteSpace(_config().SeerrUrl)) return true;
        var user = _users.GetUserById(userId);
        return user is not null && _users.GetUserDto(user).Policy?.IsAdministrator == true;
    }

    private static ChannelItemInfo Video(DownloadEntry entry, string id, string name) => new()
    {
        Id = id,
        Name = name,
        Overview = entry.Release,
        ImageUrl = entry.Poster,
        Type = ChannelItemType.Media,
        MediaType = ChannelMediaType.Video,
        ContentType = entry.IsMovie ? ChannelMediaContentType.Movie : ChannelMediaContentType.Episode,
        // Jellyfin shows a movie's year after its name: the name stays without it.
        ProductionYear = entry.IsMovie && entry.Year > 0 ? entry.Year : null,
    };

    public static string Label(DownloadEntry entry)
    {
        var title = entry.Title;
        if (entry.IsMovie || entry.Episodes.Count == 0) return title;
        if (entry.Episodes.Count == 1) return $"{title} — S{entry.Episodes[0].Season:00}E{entry.Episodes[0].Episode:00}";
        var seasons = entry.Episodes.Select(e => e.Season).Distinct().OrderBy(s => s).ToList();
        return $"{title} — {string.Join(", ", seasons.Select(s => $"S{s:00}"))} ({entry.Episodes.Count} épisodes)";
    }

    public static string FileLabel(DownloadEntry entry, QbitFile file)
    {
        var name = Path.GetFileName(file.Name);
        var match = Regex.Match(name, @"(?i)(?:^|[^a-z0-9])s(\d{1,2})e(\d{1,3})(?!\d)");
        if (!match.Success) return name;
        var (season, episode) = (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        var known = entry.Episodes.FirstOrDefault(e => e.Season == season && e.Episode == episode);
        return known is null || known.Title.Length == 0 ? $"S{season:00}E{episode:00} — {name}" : $"S{season:00}E{episode:00} — {known.Title}";
    }

    /// <summary>« No Other Land (2024) · 26 % · 1,3 Mio/s · fin dans 11 min »: renamed at each listing.</summary>
    public static string FolderName(DownloadEntry entry, QbitTorrent torrent)
    {
        var parts = new List<string> { entry.IsMovie && entry.Year > 0 ? $"{entry.Title} ({entry.Year})" : Label(entry), $"{Math.Floor(torrent.Progress * 100).ToString(CultureInfo.InvariantCulture)} %" };
        if (torrent.Progress >= 1) parts.Add("fini, en attente d'import");
        else if (torrent.DownloadSpeed > 0)
        {
            parts.Add((torrent.DownloadSpeed / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " Mio/s");
            if (torrent.Eta is > 0 and < 8640000) parts.Add("fin dans " + Duration(torrent.Eta));
        }
        else parts.Add(torrent.State.StartsWith("queued", StringComparison.Ordinal) ? "en attente dans qBittorrent" : torrent.State.StartsWith("stopped", StringComparison.Ordinal) || torrent.State.StartsWith("paused", StringComparison.Ordinal) ? "arrêté" : "aucune source pour l'instant");
        return string.Join(" · ", parts);
    }

    private static string Duration(long seconds) => seconds < 3600 ? $"{Math.Max(1, seconds / 60)} min" : $"{seconds / 3600} h {seconds % 3600 / 60:00}";
    private static ChannelItemResult Result(IReadOnlyList<ChannelItemInfo> items) => new() { Items = items, TotalRecordCount = items.Count };
}
