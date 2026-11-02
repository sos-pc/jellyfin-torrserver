using System.Text.Json;
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

public sealed class TmdbChannel : IChannel, IRequiresMediaInfoCallback, IHasCacheKey
{
    private readonly Services _services;
    private readonly SelectionStore _store;
    private readonly IMediaSourceManager _media;
    private readonly PlaybackTickets _tickets;
    public TmdbChannel(Services services, SelectionStore store, IMediaSourceManager media, PlaybackTickets tickets) { _services = services; _store = store; _media = media; _tickets = tickets; }
    public string Name => "Catalogue TMDB";
    public string Description => "Films et séries : choisissez un torrent, puis un fichier à lire avec TorrServer.";
    public string DataVersion => "2";
    public string? GetCacheKey(string? userId) => (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300).ToString();
    public string HomePageUrl => "https://www.themoviedb.org/";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.UsR;
    public bool IsEnabledFor(string userId) => _services.Config.AllowedUserIds.Length == 0 || _services.Config.AllowedUserIds.Contains(userId, StringComparer.OrdinalIgnoreCase);
    public InternalChannelFeatures GetChannelFeatures() => new() { MediaTypes = [ChannelMediaType.Video], ContentTypes = [ChannelMediaContentType.Movie, ChannelMediaContentType.Episode], AutoRefreshLevels = 0 };
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) => Task.FromResult(new DynamicImageResponse { HasImage = type == ImageType.Primary, Stream = typeof(TmdbChannel).Assembly.GetManifestResourceStream("Jellyfin.Plugin.TorrServer.logo.png"), Format = ImageFormat.Png });

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        var id = query.FolderId ?? "";
        var parts = id.Split(':');
        var items = new List<ChannelItemInfo>();
        if (id == "") return Result([Folder("popular:movie:1", "Films populaires"), Folder("popular:tv:1", "Séries populaires")]);
        if (parts[0] == "source" && parts.Length == 4)
        {
            var release = await _store.Load(parts[1], ct);
            var status = await _services.Prepare(release, ct);
            var season = ParseNonnegative(parts[2]); var episode = ParseNonnegative(parts[3]);
            foreach (var file in status.Files!.Where(x => Services.IsVideo(x.Path)).OrderByDescending(x => MatchesEpisode(x.Path, season, episode)).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
                items.Add(new ChannelItemInfo { Id = $"file:{parts[1]}:{file.Id}", Name = (MatchesEpisode(file.Path, season, episode) ? "★ " : "") + file.Path, Overview = $"{file.Length / 1073741824.0:F2} Gio — {release.Title}", Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video, ContentType = episode > 0 ? ChannelMediaContentType.Episode : ChannelMediaContentType.Movie });
            return Result(items);
        }
        if (parts[0] == "search" && parts.Length == 5)
        {
            var mediaKind = parts[1]; var tmdbId = ParsePositive(parts[2]); var season = ParseNonnegative(parts[3]); var episode = ParseNonnegative(parts[4]);
            if (mediaKind is not ("movie" or "tv")) throw new ArgumentException("Type de média invalide.");
            var detail = await _services.Tmdb($"{mediaKind}/{tmdbId}", ct);
            var title = Services.Text(detail, mediaKind == "movie" ? "title" : "name");
            var original = Services.Text(detail, mediaKind == "movie" ? "original_title" : "original_name");
            var date = Services.Text(detail, mediaKind == "movie" ? "release_date" : "first_air_date");
            var suffix = mediaKind == "movie" ? (date.Length >= 4 ? " " + date[..4] : "") : episode > 0 ? $" S{season:00}E{episode:00}" : season > 0 ? $" S{season:00}" : "";
            var releases = (await _services.Search(title + suffix, ct)).ToList();
            if (!string.IsNullOrWhiteSpace(original) && original != title) releases.AddRange(await _services.Search(original + suffix, ct));
            foreach (var release in releases.DistinctBy(x => x.Link).OrderByDescending(x => x.Seeders).Take(Math.Clamp(_services.Config.MaxResults, 1, 200)))
            {
                var key = await _store.Save(release, ct);
                var folder = Folder($"source:{key}:{season}:{episode}", $"{release.Title} — {release.Seeders} sources — {release.Size / 1073741824.0:F2} Gio — {release.Indexer}");
                folder.Overview = "Ouvrez ce torrent pour choisir le fichier à lire."; items.Add(folder);
            }
            return Result(items);
        }
        if (parts[0] == "episode" && parts.Length == 4)
        {
            var show = ParsePositive(parts[1]); var season = ParseNonnegative(parts[2]); var episode = ParsePositive(parts[3]);
            return Result([Folder($"search:tv:{show}:{season}:{episode}", "Chercher cet épisode"), Folder($"search:tv:{show}:{season}:0", "Chercher un pack de cette saison"), Folder($"search:tv:{show}:0:0", "Chercher un pack de la série")]);
        }
        string path;
        if (parts[0] == "popular" && parts.Length == 3 && parts[1] is "movie" or "tv") path = $"{parts[1]}/popular?page={Math.Clamp(ParsePositive(parts[2]), 1, 500)}";
        else if (parts[0] == "tv" && parts.Length == 2) path = $"tv/{ParsePositive(parts[1])}";
        else if (parts[0] == "season" && parts.Length == 3) path = $"tv/{ParsePositive(parts[1])}/season/{ParseNonnegative(parts[2])}";
        else throw new ArgumentException("Dossier invalide.");
        var json = await _services.Tmdb(path, ct);
        var listName = parts[0] == "tv" ? "seasons" : parts[0] == "season" ? "episodes" : "results";
        foreach (var element in json.GetProperty(listName).EnumerateArray())
        {
            ChannelItemInfo item;
            if (parts[0] == "tv")
            {
                var n = Services.Number(element, "season_number");
                item = Folder($"season:{parts[1]}:{n}", Services.Text(element, "name")); item.FolderType = ChannelFolderType.Season; item.IndexNumber = n;
            }
            else if (parts[0] == "season")
            {
                var n = Services.Number(element, "episode_number");
                item = Folder($"episode:{parts[1]}:{parts[2]}:{n}", $"E{n:00} — {Services.Text(element, "name")}"); item.IndexNumber = n;
            }
            else
            {
                var tmdbId = Services.Number(element, "id"); bool movie = parts[1] == "movie";
                item = Folder(movie ? $"search:movie:{tmdbId}:0:0" : $"tv:{tmdbId}", Services.Text(element, movie ? "title" : "name"));
                if (!movie) item.FolderType = ChannelFolderType.Series;
            }
            item.Overview = Services.Text(element, "overview");
            var poster = Services.Text(element, "poster_path"); if (poster == "") poster = Services.Text(element, "still_path");
            if (poster.StartsWith('/')) item.ImageUrl = "https://image.tmdb.org/t/p/w500" + poster;
            items.Add(item);
        }
        if (parts[0] == "popular" && ParsePositive(parts[2]) < Math.Min(500, Services.Number(json, "total_pages"))) items.Add(Folder($"popular:{parts[1]}:{ParsePositive(parts[2]) + 1}", "Page suivante →"));
        return Result(items);
    }
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        var parts = id.Split(':');
        if (parts.Length != 3 || parts[0] != "file") throw new ArgumentException("Fichier invalide.");
        var release = await _store.Load(parts[1], ct);
        var status = await _services.Prepare(release, ct);
        var file = status.Files!.SingleOrDefault(x => x.Id == ParsePositive(parts[2]) && Services.IsVideo(x.Path)) ?? throw new InvalidOperationException("Ce fichier n'est plus disponible dans le torrent.");
        var source = new MediaSourceInfo { Id = id.Replace(':', '_'), Name = file.Path, Path = Services.Endpoint(_services.Config.JellyfinInternalUrl, "TorrServerPlugin/play/" + _tickets.Issue(_services.StreamUrl(status.Hash, file.Id, file.Path))).AbsoluteUri, Protocol = MediaProtocol.Http, IsRemote = true, Container = Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant(), SupportsDirectPlay = false, SupportsDirectStream = true, SupportsTranscoding = true, Size = file.Length };
        await _media.AddMediaInfoWithProbe(source, false, id, false, false, ct);
        return [source];
    }
    public static bool MatchesEpisode(string path, int season, int episode) => episode > 0 && (Regex.IsMatch(path, $@"(?i)(?:^|[^a-z0-9])s0*{season}e0*{episode}(?!\d)") || Regex.IsMatch(path, $@"(?i)(?:^|[^a-z0-9])0*{season}x0*{episode}(?!\d)"));
    private static int ParsePositive(string value) => int.TryParse(value, out var n) && n > 0 ? n : throw new ArgumentException("Identifiant invalide.");
    private static int ParseNonnegative(string value) => int.TryParse(value, out var n) && n >= 0 ? n : throw new ArgumentException("Numéro invalide.");
    private static ChannelItemInfo Folder(string id, string name) => new() { Id = id, Name = name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container };
    private static ChannelItemResult Result(IReadOnlyList<ChannelItemInfo> items) => new() { Items = items, TotalRecordCount = items.Count };
}
