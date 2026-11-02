using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TorrServer;

public sealed record Release(string Id, string Title, string Link, int Seeders, long Size, string Indexer);
public sealed record TorrentFile(int Id, string Path, long Length);
public sealed record TorrentStatus(string Hash, [property: JsonPropertyName("file_stats")] List<TorrentFile>? Files);

public sealed class SelectionStore
{
    private readonly Func<string> _directory;
    private readonly SemaphoreSlim _gate = new(1);
    public SelectionStore(Func<string> directory) => _directory = directory;
    public async Task<string> Save(Release release, CancellationToken ct)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(release.Link))).ToLowerInvariant();
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_directory());
            var file = Path.Combine(_directory(), id + ".json");
            if (!File.Exists(file))
            {
                var temporary = file + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(release), ct);
                File.Move(temporary, file, true);
            }
            return id;
        }
        finally { _gate.Release(); }
    }
    public async Task<Release> Load(string id, CancellationToken ct)
    {
        if (id.Length != 64 || id.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Source invalide.");
        var file = Path.Combine(_directory(), id + ".json");
        if (!File.Exists(file)) throw new InvalidOperationException("Source expirée. Relancez la recherche.");
        return JsonSerializer.Deserialize<Release>(await File.ReadAllTextAsync(file, ct)) ?? throw new InvalidOperationException("Source illisible.");
    }
}

public sealed class Services
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly IHttpClientFactory _http;
    private readonly Func<PluginConfiguration> _config;
    public Services(IHttpClientFactory http, Func<PluginConfiguration> config) { _http = http; _config = config; }
    public PluginConfiguration Config => _config();
    public static Uri Endpoint(string value, string path)
    {
        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Adresse de service invalide (HTTP ou HTTPS requis).");
        return new Uri(uri, path);
    }
    public async Task<JsonElement> Tmdb(string path, CancellationToken ct)
    {
        var token = Config.TmdbReadAccessToken.Trim();
        if (token.Length == 0) token = (Environment.GetEnvironmentVariable("TMBD_API_LECTURE") ?? Environment.GetEnvironmentVariable("TMDB_API_LECTURE") ?? "").Trim();
        var key = Config.TmdbApiKey.Trim();
        if (token.Length == 0 && key.Length == 0) throw new InvalidOperationException("Configurez une clé API TMDB ou un jeton de lecture TMDB.");
        var url = "https://api.themoviedb.org/3/" + path + (path.Contains('?') ? "&" : "?") + "language=fr-FR";
        if (token.Length == 0) url += "&api_key=" + Uri.EscapeDataString(key);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Send(request, ct);
    }
    public async Task<IReadOnlyList<Release>> Search(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Config.ProwlarrApiKey)) throw new InvalidOperationException("Configurez la clé API Prowlarr.");
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(Config.ProwlarrUrl, "api/v1/search?type=search&query=" + Uri.EscapeDataString(query)));
        request.Headers.Add("X-Api-Key", Config.ProwlarrApiKey);
        var json = await Send(request, ct);
        if (json.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Réponse Prowlarr invalide.");
        return json.EnumerateArray().Where(x => Text(x, "protocol").Equals("torrent", StringComparison.OrdinalIgnoreCase)).Select(x =>
        {
            var link = Text(x, "magnetUrl");
            if (string.IsNullOrEmpty(link)) link = Text(x, "downloadUrl");
            return new Release(Text(x, "guid"), Text(x, "title"), link, Number(x, "seeders"), Long(x, "size"), Text(x, "indexer"));
        }).Where(x => !string.IsNullOrWhiteSpace(x.Link)).DistinctBy(x => x.Link).OrderByDescending(x => x.Seeders).Take(Math.Clamp(Config.MaxResults, 1, 200)).ToList();
    }
    public async Task<TorrentStatus> Prepare(Release release, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(Config.MetadataTimeoutSeconds, 5, 180)));
        ct = timeout.Token;
        TorrentStatus status;
        if (release.Link.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            using var request = TorrRequest("torrents");
            request.Content = JsonContent.Create(new { action = "add", link = release.Link, title = release.Title, save_to_db = true });
            status = ParseStatus(await Send(request, ct));
        }
        else
        {
            var bytes = await DownloadTorrent(release.Link, ct);
            using var request = TorrRequest("torrent/upload");
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(bytes), "file", "source.torrent");
            form.Add(new StringContent("true"), "save");
            form.Add(new StringContent(release.Title), "title");
            request.Content = form;
            status = ParseStatus(await Send(request, ct));
        }
        if (string.IsNullOrEmpty(status.Hash) || status.Hash.Any(c => !Uri.IsHexDigit(c))) throw new InvalidOperationException("TorrServer n'a pas retourné de hash valide.");
        while (status.Files is not { Count: > 0 })
        {
            await Task.Delay(1000, ct);
            using var request = TorrRequest("torrents");
            request.Content = JsonContent.Create(new { action = "get", hash = status.Hash });
            status = ParseStatus(await Send(request, ct));
        }
        return status;
    }
    public HttpRequestMessage TorrRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(Config.TorrServerUrl, path));
        if (!string.IsNullOrEmpty(Config.TorrServerUsername)) request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Config.TorrServerUsername + ":" + Config.TorrServerPassword)));
        return request;
    }
    public string StreamUrl(string hash, int index, string path) => Endpoint(Config.TorrServerUrl, "stream/" + Uri.EscapeDataString(Path.GetFileName(path)) + "?link=" + Uri.EscapeDataString(hash) + "&index=" + index + "&play").AbsoluteUri;
    private async Task<byte[]> DownloadTorrent(string link, CancellationToken ct)
    {
        var url = new Uri(link, UriKind.Absolute);
        var prowlarr = Endpoint(Config.ProwlarrUrl, "");
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo)) throw new InvalidOperationException("Lien torrent invalide.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (url.GetLeftPart(UriPartial.Authority) == prowlarr.GetLeftPart(UriPartial.Authority)) request.Headers.Add("X-Api-Key", Config.ProwlarrApiKey);
            using var response = await _http.CreateClient("TorrServerNoRedirect").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null) { url = new Uri(url, response.Headers.Location); continue; }
            Ensure(response);
            if (response.Content.Headers.ContentLength > 10 * 1024 * 1024) throw new InvalidOperationException("Fichier torrent trop volumineux.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + read > 10 * 1024 * 1024) throw new InvalidOperationException("Fichier torrent trop volumineux."); buffer.Write(chunk, 0, read); }
            return buffer.ToArray();
        }
        throw new InvalidOperationException("Trop de redirections du lien torrent.");
    }
    private async Task<JsonElement> Send(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.CreateClient("TorrServerNoRedirect").SendAsync(request, ct);
        Ensure(response);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.Clone();
    }
    private static void Ensure(HttpResponseMessage r) { if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Le service a retourné HTTP {(int)r.StatusCode}. Vérifiez son adresse et ses identifiants."); }
    private static TorrentStatus ParseStatus(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) e = e.GetArrayLength() == 1 ? e[0] : throw new InvalidOperationException("TorrServer a refusé le fichier torrent.");
        return e.Deserialize<TorrentStatus>(Json) ?? throw new InvalidOperationException("Réponse TorrServer invalide.");
    }
    public static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    public static int Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
    private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    public static bool IsVideo(string path) => new[] { ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".m2ts", ".webm", ".mov" }.Contains(Path.GetExtension(path).ToLowerInvariant());
}
