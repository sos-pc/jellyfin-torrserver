using System.Text.Json;

namespace Jellyfin.Plugin.TorrServer;

public sealed record DownloadEpisode(int Season, int Episode, string Title);
/// <summary>A torrent that Radarr or Sonarr is downloading, with what it is for.</summary>
public sealed record DownloadEntry(string Hash, bool IsMovie, string Title, int Year, string? Poster, string Release, int TmdbId, int TvdbId, IReadOnlyList<DownloadEpisode> Episodes);
public sealed record SeerrRequest(bool IsMovie, int TmdbId, int TvdbId, string JellyfinUserId);

/// <summary>
/// Lists what Radarr and Sonarr are downloading with qBittorrent, as the channel shows it: the *arr queues say what each
/// torrent is for, qBittorrent says how far it is, and Seerr says who asked for it.
/// </summary>
public sealed class DownloadCatalog
{
    private static readonly TimeSpan RequestsLifetime = TimeSpan.FromMinutes(1);
    private readonly IHttpClientFactory _http;
    private readonly Func<PluginConfiguration> _config;
    private readonly QbitClient _qbit;
    private readonly SemaphoreSlim _requestsGate = new(1);
    private (DateTime At, IReadOnlyList<SeerrRequest> Requests)? _requests;

    public DownloadCatalog(IHttpClientFactory http, Func<PluginConfiguration> config, QbitClient qbit) { _http = http; _config = config; _qbit = qbit; }
    private PluginConfiguration Config => _config();
    public QbitClient Qbit => _qbit;

    /// <summary>Downloads this user may see: those they asked for in Seerr, or all of them for someone who sees everything.</summary>
    public async Task<IReadOnlyList<(DownloadEntry Entry, QbitTorrent Torrent)>> List(string userId, bool seesAll, CancellationToken ct)
    {
        var entries = await Queue(ct);
        if (!seesAll && !string.IsNullOrWhiteSpace(Config.SeerrUrl))
        {
            var requests = await Requests(ct);
            entries = entries.Where(e => Visible(e, userId, requests)).ToList();
        }
        var torrents = await _qbit.Torrents(entries.Select(e => e.Hash), ct);
        return entries.Where(e => torrents.ContainsKey(e.Hash)).Select(e => (e, torrents[e.Hash]))
            .OrderByDescending(x => x.Item2.DownloadSpeed > 0).ThenByDescending(x => x.Item2.Progress).ThenBy(x => x.e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A download is shown to whoever asked for its movie or series in Seerr.</summary>
    public static bool Visible(DownloadEntry entry, string userId, IReadOnlyList<SeerrRequest> requests)
    {
        var user = NormalizeId(userId);
        return requests.Any(r => r.JellyfinUserId == user && r.IsMovie == entry.IsMovie
            && (entry.IsMovie ? r.TmdbId > 0 && r.TmdbId == entry.TmdbId : (r.TvdbId > 0 && r.TvdbId == entry.TvdbId) || (r.TmdbId > 0 && r.TmdbId == entry.TmdbId)));
    }

    public static string NormalizeId(string id) => Guid.TryParse(id, out var guid) ? guid.ToString("N") : id.Trim().ToLowerInvariant();

    /// <summary>Torrent downloads of the Radarr and Sonarr queues, one entry per torrent.</summary>
    public async Task<List<DownloadEntry>> Queue(CancellationToken ct)
    {
        var entries = new Dictionary<string, DownloadEntry>();
        if (!string.IsNullOrWhiteSpace(Config.RadarrUrl))
        {
            foreach (var record in await Records(Config.RadarrUrl, Config.RadarrApiKey, "api/v3/queue?page=1&pageSize=500&includeMovie=true", ct))
            {
                if (!IsTorrent(record, out var hash) || !record.TryGetProperty("movie", out var movie)) continue;
                entries.TryAdd(hash, new DownloadEntry(hash, true, Services.Text(movie, "title"), Services.Number(movie, "year"), Poster(movie), Services.Text(record, "title"), Services.Number(movie, "tmdbId"), 0, []));
            }
        }
        if (!string.IsNullOrWhiteSpace(Config.SonarrUrl))
        {
            var episodes = new Dictionary<string, List<DownloadEpisode>>();
            foreach (var record in await Records(Config.SonarrUrl, Config.SonarrApiKey, "api/v3/queue?page=1&pageSize=1000&includeSeries=true&includeEpisode=true", ct))
            {
                if (!IsTorrent(record, out var hash) || !record.TryGetProperty("series", out var series)) continue;
                if (!episodes.TryGetValue(hash, out var list)) episodes[hash] = list = [];
                if (record.TryGetProperty("episode", out var episode)) list.Add(new DownloadEpisode(Services.Number(episode, "seasonNumber"), Services.Number(episode, "episodeNumber"), Services.Text(episode, "title")));
                entries.TryAdd(hash, new DownloadEntry(hash, false, Services.Text(series, "title"), Services.Number(series, "year"), Poster(series), Services.Text(record, "title"), Services.Number(series, "tmdbId"), Services.Number(series, "tvdbId"), list));
            }
        }
        return entries.Values.ToList();
    }

    /// <summary>Seerr requests with the Jellyfin account that made them, kept a minute.</summary>
    public async Task<IReadOnlyList<SeerrRequest>> Requests(CancellationToken ct)
    {
        if (_requests is { } cached && DateTime.UtcNow - cached.At < RequestsLifetime) return cached.Requests;
        await _requestsGate.WaitAsync(ct);
        try
        {
            if (_requests is { } again && DateTime.UtcNow - again.At < RequestsLifetime) return again.Requests;
            var requests = new List<SeerrRequest>();
            const int take = 100;
            for (var page = 0; page < 50; page++)
            {
                var json = await Get(Config.SeerrUrl, Config.SeerrApiKey, $"api/v1/request?take={take}&skip={page * take}&filter=all&sort=added", ct);
                if (!json.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) break;
                foreach (var r in results.EnumerateArray())
                {
                    var userId = r.TryGetProperty("requestedBy", out var by) ? Services.Text(by, "jellyfinUserId") : "";
                    if (userId.Length == 0 || !r.TryGetProperty("media", out var media)) continue;
                    var type = Services.Text(r, "type");
                    if (type.Length == 0) type = Services.Text(media, "mediaType");
                    requests.Add(new SeerrRequest(type == "movie", Services.Number(media, "tmdbId"), Services.Number(media, "tvdbId"), NormalizeId(userId)));
                }
                if (results.GetArrayLength() < take) break;
            }
            _requests = (DateTime.UtcNow, requests);
            return requests;
        }
        finally { _requestsGate.Release(); }
    }

    private static bool IsTorrent(JsonElement record, out string hash)
    {
        hash = QbitClient.NormalizeHash(Services.Text(record, "downloadId"));
        return hash.Length > 0 && Services.Text(record, "protocol").Equals("torrent", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Poster(JsonElement media)
    {
        if (!media.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        foreach (var image in images.EnumerateArray())
        {
            if (!Services.Text(image, "coverType").Equals("poster", StringComparison.OrdinalIgnoreCase)) continue;
            var url = Services.Text(image, "remoteUrl");
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https") return url;
        }
        return null;
    }

    private async Task<IReadOnlyList<JsonElement>> Records(string url, string key, string path, CancellationToken ct)
    {
        var json = await Get(url, key, path, ct);
        return json.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array ? records.EnumerateArray().ToList() : [];
    }

    public async Task<JsonElement> Get(string url, string key, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Services.Endpoint(url, path));
        request.Headers.Add("X-Api-Key", key);
        using var response = await _http.CreateClient("TorrServerNoRedirect").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{new Uri(url).Host} a retourné HTTP {(int)response.StatusCode}. Vérifiez son adresse et sa clé API.");
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.Clone();
    }
}
