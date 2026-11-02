using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Browse TMDB without creating or downloading media files.</summary>
public sealed class TmdbChannel : IChannel
{
    private readonly IHttpClientFactory _clients;
    public TmdbChannel(IHttpClientFactory clients) => _clients = clients;
    public string Name => "Catalogue TMDB";
    public string Description => "Films et séries populaires";
    public string DataVersion => "1";
    public string HomePageUrl => "https://www.themoviedb.org/";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId) => true;
    public InternalChannelFeatures GetChannelFeatures() => new() { MediaTypes = [ChannelMediaType.Video], ContentTypes = [ChannelMediaContentType.Movie, ChannelMediaContentType.Episode], MaxPageSize = 20 };
    public IEnumerable<ImageType> GetSupportedChannelImages() => [];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) => Task.FromResult(new DynamicImageResponse { HasImage = false });

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(query.FolderId))
            return new ChannelItemResult { Items = [Folder("movies", "Films"), Folder("series", "Séries")], TotalRecordCount = 2 };
        var parts = query.FolderId.Split(':');
        string path;
        if (query.FolderId == "movies") path = "movie/popular";
        else if (query.FolderId == "series") path = "tv/popular";
        else if (parts.Length == 2 && parts[0] == "tv" && int.TryParse(parts[1], out var showId) && showId > 0) path = $"tv/{showId}";
        else if (parts.Length == 3 && parts[0] == "season" && int.TryParse(parts[1], out showId) && showId > 0 && int.TryParse(parts[2], out var season) && season >= 0) path = $"tv/{showId}/season/{season}";
        else return new ChannelItemResult();
        var key = Plugin.Instance?.Configuration.TmdbApiKey;
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Configurez la clé API TMDB dans les paramètres du plugin.");
        var page = Math.Clamp((query.StartIndex ?? 0) / 20 + 1, 1, 500);
        var uri = $"https://api.themoviedb.org/3/{path}?api_key={Uri.EscapeDataString(key)}&language=fr-FR&page={page}";
        using var response = await _clients.CreateClient().GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        var listName = parts[0] == "tv" ? "seasons" : parts[0] == "season" ? "episodes" : "results";
        if (!root.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array) return new ChannelItemResult();
        var items = new List<ChannelItemInfo>();
        foreach (var element in list.EnumerateArray())
        {
            if (parts[0] == "tv")
            {
                if (!Number(element, "season_number", out var n)) continue;
                var f = Folder($"season:{parts[1]}:{n}", Text(element, "name") ?? $"Saison {n}");
                f.FolderType = ChannelFolderType.Season; f.IndexNumber = n; f.ImageUrl = Poster(element); items.Add(f);
            }
            else if (parts[0] == "season")
            {
                if (!Number(element, "episode_number", out var n)) continue;
                items.Add(new ChannelItemInfo { Id = $"episode:{parts[1]}:{parts[2]}:{n}", Name = Text(element, "name") ?? $"Épisode {n}", Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video, ContentType = ChannelMediaContentType.Episode, IndexNumber = n, ParentIndexNumber = int.Parse(parts[2], CultureInfo.InvariantCulture), Overview = Text(element, "overview"), ImageUrl = Poster(element) });
            }
            else
            {
                if (!Number(element, "id", out var id)) continue;
                bool movie = query.FolderId == "movies";
                var item = new ChannelItemInfo { Id = movie ? $"movie:{id}" : $"tv:{id}", Name = Text(element, movie ? "title" : "name") ?? id.ToString(CultureInfo.InvariantCulture), Type = movie ? ChannelItemType.Media : ChannelItemType.Folder, MediaType = ChannelMediaType.Video, ContentType = movie ? ChannelMediaContentType.Movie : ChannelMediaContentType.Episode, FolderType = ChannelFolderType.Series, Overview = Text(element, "overview"), ImageUrl = Poster(element) };
                item.ProviderIds["Tmdb"] = id.ToString(CultureInfo.InvariantCulture); items.Add(item);
            }
        }
        var total = Number(root, "total_results", out var count) ? Math.Min(count, 10000) : items.Count;
        return new ChannelItemResult { Items = items, TotalRecordCount = total };
    }
    private static ChannelItemInfo Folder(string id, string name) => new() { Id = id, Name = name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container };
    private static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Number(JsonElement e, string key, out int n) { n = 0; return e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out n); }
    private static string? Poster(JsonElement e) { var p = Text(e, "poster_path") ?? Text(e, "still_path"); return p is null || !p.StartsWith('/') ? null : "https://image.tmdb.org/t/p/w500" + p; }
}
