using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Jellyfin.Plugin.TorrServer;

public sealed record QbitTorrent(string Hash, string Name, string State, double Progress, long DownloadSpeed, long Eta, string SavePath, string DownloadPath, string ContentPath, bool Sequential, bool FirstLastPieces);
public sealed record QbitFile(int Index, string Name, long Size, double Progress, int Priority, int FirstPiece, int LastPiece);
/// <summary>A file of a torrent as Jellyfin reads it: its path on disk and the pieces that hold it.</summary>
public sealed record QbitFileLocation(string Hash, QbitFile File, string Path, long PieceSize);

/// <summary>
/// qBittorrent Web API client for watching a download while it arrives. It reads torrents and their pieces, and only
/// raises the priority of the file being watched: it never stops, removes or deselects anything, so every download
/// still completes and seeds as the *arr and the trackers expect.
/// </summary>
public sealed class QbitClient
{
    private readonly IHttpClientFactory _http;
    private readonly Func<PluginConfiguration> _config;
    private readonly SemaphoreSlim _login = new(1);
    private readonly ConcurrentDictionary<string, (DateTime At, int[] States)> _pieces = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _prioritizing = new();
    private string? _cookie;

    public QbitClient(IHttpClientFactory http, Func<PluginConfiguration> config) { _http = http; _config = config; }
    private PluginConfiguration Config => _config();
    public bool Configured => !string.IsNullOrWhiteSpace(Config.QbittorrentUrl);

    public async Task<IReadOnlyDictionary<string, QbitTorrent>> Torrents(IEnumerable<string> hashes, CancellationToken ct)
    {
        var wanted = hashes.Select(NormalizeHash).Distinct().ToList();
        var result = new Dictionary<string, QbitTorrent>();
        if (wanted.Count == 0) return result;
        var json = await Get("api/v2/torrents/info?hashes=" + Uri.EscapeDataString(string.Join('|', wanted)), ct);
        foreach (var t in json.EnumerateArray())
        {
            var hash = NormalizeHash(Services.Text(t, "hash"));
            if (!wanted.Contains(hash)) continue;
            result[hash] = new QbitTorrent(hash, Services.Text(t, "name"), Services.Text(t, "state"), Double(t, "progress"), Long(t, "dlspeed"), Long(t, "eta"),
                Services.Text(t, "save_path"), Services.Text(t, "download_path"), Services.Text(t, "content_path"), Bool(t, "seq_dl"), Bool(t, "f_l_piece_prio"));
        }
        return result;
    }

    public async Task<IReadOnlyList<QbitFile>> Files(string hash, CancellationToken ct)
    {
        var json = await Get("api/v2/torrents/files?hash=" + NormalizeHash(hash), ct);
        var files = new List<QbitFile>();
        var position = 0;
        foreach (var f in json.EnumerateArray())
        {
            var index = f.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : position;
            int first = -1, last = -1;
            if (f.TryGetProperty("piece_range", out var range) && range.ValueKind == JsonValueKind.Array && range.GetArrayLength() == 2) { first = range[0].GetInt32(); last = range[1].GetInt32(); }
            files.Add(new QbitFile(index, Services.Text(f, "name"), Long(f, "size"), Double(f, "progress"), Services.Number(f, "priority"), first, last));
            position++;
        }
        return files;
    }

    /// <summary>The file of a torrent on Jellyfin's disk, with the size of its pieces.</summary>
    public async Task<QbitFileLocation> Locate(string hash, int fileIndex, CancellationToken ct)
    {
        hash = NormalizeHash(hash);
        var torrent = (await Torrents([hash], ct)).GetValueOrDefault(hash) ?? throw new InvalidOperationException("Ce téléchargement n'est plus dans qBittorrent.");
        var file = (await Files(hash, ct)).FirstOrDefault(f => f.Index == fileIndex) ?? throw new InvalidOperationException("Ce fichier n'est plus dans le torrent.");
        if (file.FirstPiece < 0 || file.Size <= 0) throw new InvalidOperationException("qBittorrent n'a pas encore les métadonnées de ce torrent.");
        var properties = await Get("api/v2/torrents/properties?hash=" + hash, ct);
        var pieceSize = Long(properties, "piece_size");
        if (pieceSize <= 0) throw new InvalidOperationException("qBittorrent n'a pas donné la taille des morceaux.");
        var root = torrent.Progress < 1 && !string.IsNullOrEmpty(torrent.DownloadPath) ? torrent.DownloadPath : torrent.SavePath;
        var path = MapPath(root.TrimEnd('/') + "/" + file.Name);
        if (!File.Exists(path) && File.Exists(path + ".!qB")) path += ".!qB";
        return new QbitFileLocation(hash, file, path, pieceSize);
    }

    /// <summary>
    /// Brings the bytes of the watched file first: sequential order, first and last pieces first (the index of most
    /// containers is at the end), and the highest priority for this file. The other files keep their priority, so the
    /// torrent still completes.
    /// </summary>
    public async Task Prioritize(string hash, int fileIndex, CancellationToken ct)
    {
        hash = NormalizeHash(hash);
        var gate = _prioritizing.GetOrAdd(hash, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        try
        {
            var torrent = (await Torrents([hash], ct)).GetValueOrDefault(hash) ?? throw new InvalidOperationException("Ce téléchargement n'est plus dans qBittorrent.");
            // These two calls are toggles: only send them when the option is off.
            if (!torrent.Sequential) await Post("api/v2/torrents/toggleSequentialDownload", new() { ["hashes"] = hash }, ct);
            if (!torrent.FirstLastPieces) await Post("api/v2/torrents/toggleFirstLastPiecePrio", new() { ["hashes"] = hash }, ct);
            var file = (await Files(hash, ct)).FirstOrDefault(f => f.Index == fileIndex);
            if (file is { Priority: > 0 and < MaximumPriority })
                await Post("api/v2/torrents/filePrio", new() { ["hash"] = hash, ["id"] = fileIndex.ToString(CultureInfo.InvariantCulture), ["priority"] = MaximumPriority.ToString(CultureInfo.InvariantCulture) }, ct);
        }
        finally { gate.Release(); }
    }

    public const int MaximumPriority = 7;

    /// <summary>Waits until the pieces that may hold bytes [start, end] of the file are downloaded and checked.</summary>
    public async Task WaitForBytes(QbitFileLocation file, long start, long end, CancellationToken ct)
    {
        var (first, last) = PieceMath.Covering(file, start, end);
        while (!PieceMath.AllDownloaded(await PieceStates(file.Hash, ct), first, last)) await Task.Delay(500, ct);
    }

    public async Task<int[]> PieceStates(string hash, CancellationToken ct)
    {
        hash = NormalizeHash(hash);
        if (_pieces.TryGetValue(hash, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMilliseconds(400)) return cached.States;
        var json = await Get("api/v2/torrents/pieceStates?hash=" + hash, ct);
        var states = json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0).ToArray() : [];
        _pieces[hash] = (DateTime.UtcNow, states);
        return states;
    }

    public async Task<string> Version(CancellationToken ct)
    {
        using var response = await Send(() => new HttpRequestMessage(HttpMethod.Get, Services.Endpoint(Config.QbittorrentUrl, "api/v2/app/version")), ct);
        Ensure(response);
        return (await response.Content.ReadAsStringAsync(ct)).Trim();
    }

    public string MapPath(string path)
    {
        var from = Config.QbittorrentPathPrefix.Trim();
        return from.Length > 0 && path.StartsWith(from, StringComparison.Ordinal) ? Config.JellyfinPathPrefix.Trim() + path[from.Length..] : path;
    }

    public static string NormalizeHash(string hash) => hash.Trim().ToLowerInvariant();

    private async Task<JsonElement> Get(string path, CancellationToken ct)
    {
        using var response = await Send(() => new HttpRequestMessage(HttpMethod.Get, Services.Endpoint(Config.QbittorrentUrl, path)), ct);
        Ensure(response);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.Clone();
    }

    private async Task Post(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await Send(() => new HttpRequestMessage(HttpMethod.Post, Services.Endpoint(Config.QbittorrentUrl, path)) { Content = new FormUrlEncodedContent(form) }, ct);
        Ensure(response);
    }

    /// <summary>Sends a request with the session cookie, and logs in again once when the session has expired.</summary>
    private async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var cookie = _cookie ?? await Login(ct);
            using var request = make();
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.Forbidden) return response;
            response.Dispose();
            Interlocked.CompareExchange(ref _cookie, null, cookie);
        }
        throw new InvalidOperationException("qBittorrent refuse la session. Vérifiez l'utilisateur et le mot de passe.");
    }

    private async Task<string> Login(CancellationToken ct)
    {
        await _login.WaitAsync(ct);
        try
        {
            if (_cookie is not null) return _cookie;
            using var request = new HttpRequestMessage(HttpMethod.Post, Services.Endpoint(Config.QbittorrentUrl, "api/v2/auth/login"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = Config.QbittorrentUsername, ["password"] = Config.QbittorrentPassword }),
            };
            using var response = await Client.SendAsync(request, ct);
            var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            // The session cookie is the first name=value of Set-Cookie (SID unless qBittorrent was told otherwise).
            var cookie = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.Select(v => v.Split(';')[0].Trim()).FirstOrDefault(v => v.Contains('=')) : null;
            if (!response.IsSuccessStatusCode || cookie is null || body.StartsWith("Fails", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Connexion à qBittorrent refusée (HTTP {(int)response.StatusCode}). Vérifiez l'adresse, l'utilisateur et le mot de passe.");
            return _cookie = cookie;
        }
        finally { _login.Release(); }
    }

    private HttpClient Client => _http.CreateClient("TorrServerQbit");
    private static void Ensure(HttpResponseMessage r) { if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"qBittorrent a retourné HTTP {(int)r.StatusCode}."); }
    internal static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    internal static double Double(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    internal static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

public static class PieceMath
{
    /// <summary>
    /// Pieces that may hold bytes [start, end] of a file. qBittorrent gives a file's first and last piece but not where
    /// the file starts inside the first one, so a byte at position p is in piece first + p / size or in the next one:
    /// both are awaited. Waiting for one piece too many costs a moment; reading a piece not yet written would hand the
    /// player zeros.
    /// </summary>
    public static (int First, int Last) Covering(QbitFileLocation file, long start, long end)
    {
        var size = file.PieceSize;
        var first = (int)Math.Min(file.File.LastPiece, file.File.FirstPiece + start / size);
        var last = (int)Math.Min(file.File.LastPiece, file.File.FirstPiece + end / size + 1);
        return (first, last);
    }

    /// <summary>qBittorrent's piece states: 0 missing, 1 downloading, 2 downloaded and checked.</summary>
    public static bool AllDownloaded(int[] states, int first, int last)
    {
        if (first < 0 || last >= states.Length) return false;
        for (var i = first; i <= last; i++) if (states[i] != 2) return false;
        return true;
    }
}
