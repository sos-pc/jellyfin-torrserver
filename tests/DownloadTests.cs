using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using Jellyfin.Plugin.TorrServer;

/// <summary>« En téléchargement » against controlled qBittorrent, Radarr, Sonarr and Seerr answers.</summary>
public sealed class DownloadTests : IDisposable
{
    private const string Movie = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Pack = "cccccccccccccccccccccccccccccccccccccccc";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ts-dl-" + Guid.NewGuid());
    private readonly PluginConfiguration _config = new()
    {
        QbittorrentUrl = "http://qbit:8080", QbittorrentUsername = "user", QbittorrentPassword = "secret",
        RadarrUrl = "http://radarr:7878", RadarrApiKey = "radarr-key", SonarrUrl = "http://sonarr:8989", SonarrApiKey = "sonarr-key",
        DownloadStartTimeoutSeconds = 1,
    };
    private readonly List<(string Method, string Path, string Body, string Cookie)> _requests = [];
    private readonly HashSet<string> _expiredCookies = [];
    private int _logins;
    private int _pieceCalls;
    private Func<int, int[]> _pieces = _ => Enumerable.Repeat(2, 7).ToArray();
    private bool _sequential = true, _firstLast = true;
    private int _moviePriority = 1;
    private string _movieName = "film.mkv";

    public DownloadTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "film.mkv"), Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
    }

    /// <summary>A 100-byte MP4 whose top-level boxes come in the given order, the last one filling the rest.</summary>
    private void WriteMp4(params string[] boxes)
    {
        _movieName = "film.mp4";
        var bytes = new List<byte>();
        for (var i = 0; i < boxes.Length; i++)
        {
            var size = i == boxes.Length - 1 ? 100 - bytes.Count : 16;
            bytes.AddRange([0, 0, 0, (byte)size]);
            bytes.AddRange(Encoding.ASCII.GetBytes(boxes[i]));
            bytes.AddRange(new byte[size - 8]);
        }
        File.WriteAllBytes(Path.Combine(_directory, _movieName), bytes.ToArray());
    }

    [Fact]
    public void Mp4IndexIsFoundBeforeTheMediaData()
    {
        static byte[] Mp4(params (string Type, uint Size)[] boxes) => boxes.SelectMany(b => new byte[] { (byte)(b.Size >> 24), (byte)(b.Size >> 16), (byte)(b.Size >> 8), (byte)b.Size }.Concat(Encoding.ASCII.GetBytes(b.Type)).Concat(new byte[Math.Max(0, (int)b.Size - 8)])).ToArray();
        Assert.True(Containers.Mp4IndexFirst(Mp4(("ftyp", 16), ("free", 12), ("moov", 8))));
        Assert.False(Containers.Mp4IndexFirst(Mp4(("ftyp", 16), ("mdat", 8))));
        Assert.False(Containers.Mp4IndexFirst(Mp4(("ftyp", 16))));
        Assert.False(Containers.Mp4IndexFirst(Mp4(("ftyp", 0))));
        Assert.True(Containers.NeedsTail("film.mp4", Mp4(("ftyp", 16), ("mdat", 8))));
        Assert.False(Containers.NeedsTail("film.mp4", Mp4(("ftyp", 16), ("moov", 8))));
        Assert.False(Containers.NeedsTail("film.mkv", []));
    }

    [Fact]
    public async Task RelayDoesNotHoldThePlayerOnTheOptionalEndOfAMatroska()
    {
        _pieces = _ => [2, 2, 2, 2, 2, 0, 0];
        var tickets = new DownloadTickets();
        var context = new DefaultHttpContext();
        var lifetime = new Lifetime();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Request.Method = "GET";
        context.Request.Headers.Range = "bytes=90-";
        context.Response.Body = new MemoryStream();
        var controller = new DownloadRelayController(tickets, Qbit()) { ControllerContext = new ControllerContext { HttpContext = context } };
        await controller.Play(tickets.Issue(Movie, 0), default);
        // ffmpeg takes a 416 for the end of the whole file; a response cut before its body only fails that read.
        Assert.Equal(206, context.Response.StatusCode);
        Assert.True(lifetime.Aborted);
        Assert.Equal(0, context.Response.Body.Length);
    }

    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public bool Aborted;
        public CancellationToken RequestAborted { get; set; }
        public void Abort() => Aborted = true;
    }

    [Fact]
    public async Task MatroskaPlaysFromItsStartAlone()
    {
        _pieces = _ => [2, 2, 2, 2, 2, 0, 0];
        var probe = DispatchProxy.Create<IMediaSourceManager, ProbeProxy>();
        Assert.Single(await Channel(probe).GetChannelItemMediaInfo($"file:{Movie}:0", default));
        Assert.True(((ProbeProxy)(object)probe).Probed);
    }

    [Fact]
    public async Task Mp4WithItsIndexAtTheEndWaitsForTheEnd()
    {
        WriteMp4("ftyp", "mdat");
        _pieces = _ => [2, 2, 2, 2, 2, 0, 0];
        var probe = DispatchProxy.Create<IMediaSourceManager, ProbeProxy>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Channel(probe).GetChannelItemMediaInfo($"file:{Movie}:0", default));
        Assert.Contains("index à la fin", error.Message);
        Assert.False(((ProbeProxy)(object)probe).Probed);
    }

    [Fact]
    public async Task FastStartMp4PlaysFromItsStartAlone()
    {
        WriteMp4("ftyp", "moov", "mdat");
        _pieces = _ => [2, 2, 2, 2, 2, 0, 0];
        var probe = DispatchProxy.Create<IMediaSourceManager, ProbeProxy>();
        Assert.Single(await Channel(probe).GetChannelItemMediaInfo($"file:{Movie}:0", default));
    }

    [Theory]
    [InlineData(0, 99, 10, 11)]
    [InlineData(150, 250, 11, 13)]
    [InlineData(1990, 2050, 20, 20)]
    public void PiecesCoverTheNextPieceToo(long start, long end, int first, int last)
    {
        var file = new QbitFileLocation(Movie, new QbitFile(0, "f.mkv", 1050, 0, 1, 10, 20), "/f.mkv", 100);
        Assert.Equal((first, last), PieceMath.Covering(file, start, end));
    }

    [Fact]
    public void OnlyCheckedPiecesCount()
    {
        Assert.True(PieceMath.AllDownloaded([2, 2, 1], 0, 1));
        Assert.False(PieceMath.AllDownloaded([2, 2, 1], 0, 2));
        Assert.False(PieceMath.AllDownloaded([2, 2], 1, 2));
    }

    [Theory]
    [InlineData(null, 0, 999, false)]
    [InlineData("bytes=0-", 0, 999, true)]
    [InlineData("bytes=100-199", 100, 199, true)]
    [InlineData("bytes=900-5000", 900, 999, true)]
    [InlineData("bytes=-100", 900, 999, true)]
    public void RangesAreResolved(string? header, long start, long end, bool partial) => Assert.Equal(new ByteRange(start, end, partial), ByteRange.Parse(header, 1000));

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("items=0-1")]
    [InlineData("bytes=5-2")]
    public void UnsatisfiableRangesAreRefused(string header) => Assert.Null(ByteRange.Parse(header, 1000));

    [Fact]
    public async Task RelayWaitsForPiecesThenServesTheExactBytes()
    {
        _pieces = call => Enumerable.Repeat(call <= 2 ? 0 : 2, 7).ToArray();
        var tickets = new DownloadTickets();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Headers.Range = "bytes=10-49";
        context.Response.Body = new MemoryStream();
        var controller = new DownloadRelayController(tickets, Qbit()) { ControllerContext = new ControllerContext { HttpContext = context } };
        await controller.Play(tickets.Issue(Movie, 0), default);
        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal("bytes 10-49/100", context.Response.Headers.ContentRange);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges);
        Assert.Equal(Enumerable.Range(10, 40).Select(i => (byte)i).ToArray(), ((MemoryStream)context.Response.Body).ToArray());
        Assert.True(_pieceCalls >= 3, "the relay must wait until the pieces are checked");
        Assert.All(_requests.Where(r => r.Path.StartsWith("/api/v2/torrents", StringComparison.Ordinal)), r => Assert.Equal("SID=session-1", r.Cookie));
    }

    [Fact]
    public async Task RelayRejectsUnknownTicketsAndBadRanges()
    {
        var tickets = new DownloadTickets();
        var unknown = new DownloadRelayController(tickets, Qbit()) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        await unknown.Play("unknown", default);
        Assert.Equal(404, unknown.Response.StatusCode);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Headers.Range = "bytes=500-";
        var outside = new DownloadRelayController(tickets, Qbit()) { ControllerContext = new ControllerContext { HttpContext = context } };
        await outside.Play(tickets.Issue(Movie, 0), default);
        Assert.Equal(416, context.Response.StatusCode);
        Assert.Equal("bytes */100", context.Response.Headers.ContentRange);
    }

    [Fact]
    public async Task PrioritizingNeverDeselectsAndTogglesOnlyWhatIsOff()
    {
        _sequential = false;
        _firstLast = false;
        var qbit = Qbit();
        await qbit.Prioritize(Movie, 0, default);
        var posts = _requests.Where(r => r.Method == "POST" && r.Path != "/api/v2/auth/login").ToList();
        Assert.Equal(new[] { "/api/v2/torrents/toggleSequentialDownload", "/api/v2/torrents/toggleFirstLastPiecePrio", "/api/v2/torrents/filePrio" }, posts.Select(r => r.Path));
        Assert.Contains("priority=7", posts[2].Body);
        Assert.Contains("id=0", posts[2].Body);
        Assert.DoesNotContain(_requests, r => r.Body.Contains("priority=0"));
        _requests.Clear();
        _sequential = _firstLast = true;
        _moviePriority = 7;
        await qbit.Prioritize(Movie, 0, default);
        Assert.DoesNotContain(_requests, r => r.Method == "POST" && r.Path != "/api/v2/auth/login");
    }

    [Fact]
    public async Task ExpiredSessionLogsInAgain()
    {
        var qbit = Qbit();
        await qbit.Torrents([Movie], default);
        _expiredCookies.Add("SID=session-1");
        Assert.Single(await qbit.Torrents([Movie], default));
        Assert.Equal(2, _logins);
    }

    [Fact]
    public async Task EachUserSeesTheDownloadsTheyAskedFor()
    {
        _config.SeerrUrl = "http://seerr:5055";
        _config.SeerrApiKey = "seerr-key";
        var catalog = Catalog();
        var asker = Guid.Parse("11111111-2222-3333-4444-555555555555").ToString("N");
        Assert.Equal(new[] { Movie, Pack }, (await catalog.List(asker, false, default)).Select(x => x.Entry.Hash).OrderBy(h => h));
        Assert.Empty(await catalog.List(Guid.NewGuid().ToString("N"), false, default));
        Assert.Equal(3, (await catalog.List(Guid.NewGuid().ToString("N"), true, default)).Count);
        var pack = (await catalog.List(asker, false, default)).Single(x => x.Entry.Hash == Pack).Entry;
        Assert.Equal("Test Show — S01 (2 épisodes)", DownloadsChannel.Label(pack));
    }

    [Fact]
    public async Task EachDownloadIsAFolderNamedWithItsLiveProgress()
    {
        var channel = Channel(DispatchProxy.Create<IMediaSourceManager, ProbeProxy>());
        var root = await channel.GetChannelItems(new() { UserId = Guid.NewGuid() }, default);
        // Jellyfin renames a channel folder at each listing but never a channel video: the progress goes in folder names.
        var filmFolder = Assert.Single(root.Items, i => i.Id == $"torrent:{Movie}");
        Assert.Equal(ChannelItemType.Folder, filmFolder.Type);
        Assert.Equal("Test Film (2026) · 40 % · 2,0 Mio/s · fin dans 3 min", filmFolder.Name);
        var film = Assert.Single((await channel.GetChannelItems(new() { UserId = Guid.NewGuid(), FolderId = filmFolder.Id }, default)).Items);
        Assert.Equal($"file:{Movie}:0", film.Id);
        Assert.Equal(ChannelItemType.Media, film.Type);
        Assert.Equal("Test Film", film.Name);
        Assert.Equal(2026, film.ProductionYear);
        var pack = Assert.Single(root.Items, i => i.Id == $"torrent:{Pack}");
        Assert.StartsWith("Test Show — S01 (2 épisodes) · 50 % · 1,0 Mio/s", pack.Name);
        var episodes = await channel.GetChannelItems(new() { UserId = Guid.NewGuid(), FolderId = pack.Id }, default);
        Assert.Equal(new[] { "S01E01 — Pilot", "S01E02 — Second" }, episodes.Items.Select(i => i.Name));
        Assert.Equal(new[] { $"file:{Pack}:1", $"file:{Pack}:0" }, episodes.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task PlaybackWaitsForHeadAndTailThenProbesThroughTheRelay()
    {
        var probe = DispatchProxy.Create<IMediaSourceManager, ProbeProxy>();
        var tickets = new DownloadTickets();
        var channel = Channel(probe, tickets);
        _moviePriority = 1;
        var source = Assert.Single(await channel.GetChannelItemMediaInfo($"file:{Movie}:0", default));
        Assert.Equal("film.mkv", source.Name);
        Assert.True(Guid.TryParseExact(source.Id, "N", out _), "Jellyfin 12.4 parses the media source id as a Guid for HLS");
        Assert.Equal("mkv", source.Container);
        Assert.False(source.SupportsDirectPlay);
        Assert.Equal((Movie, 0), tickets.Get(new Uri(source.Path).Segments.Last()));
        Assert.True(((ProbeProxy)(object)probe).Probed);
        Assert.Contains(_requests, r => r.Path == "/api/v2/torrents/filePrio" && r.Body.Contains("priority=7"));
    }

    [Fact]
    public async Task PlaybackExplainsWhenTheStartIsNotThereYet()
    {
        _pieces = _ => Enumerable.Repeat(0, 7).ToArray();
        var probe = DispatchProxy.Create<IMediaSourceManager, ProbeProxy>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Channel(probe).GetChannelItemMediaInfo($"file:{Movie}:0", default));
        Assert.Contains("pas encore téléchargé", error.Message);
        Assert.False(((ProbeProxy)(object)probe).Probed);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private QbitClient Qbit() => new(new Factory(Handle), () => _config);
    private DownloadCatalog Catalog() => new(new Factory(Handle), () => _config, Qbit());
    private DownloadsChannel Channel(IMediaSourceManager media, DownloadTickets? tickets = null) => new(Catalog(), tickets ?? new DownloadTickets(), media, DispatchProxy.Create<IUserManager, NoUsers>(), () => _config);

    private async Task<HttpResponseMessage> Handle(HttpRequestMessage request)
    {
        var url = request.RequestUri!;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
        var cookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : "";
        lock (_requests) _requests.Add((request.Method.Method, url.AbsolutePath, body, cookie));
        switch (url.Host)
        {
            case "qbit":
                if (url.AbsolutePath == "/api/v2/auth/login")
                {
                    Assert.Contains("username=user", body);
                    var login = Ok("Ok.");
                    login.Headers.Add("Set-Cookie", $"SID=session-{++_logins}; HttpOnly; path=/");
                    return login;
                }
                if (cookie.Length == 0 || _expiredCookies.Contains(cookie)) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                return url.AbsolutePath switch
                {
                    "/api/v2/torrents/info" => Ok(Torrents()),
                    "/api/v2/torrents/files" => Ok(url.Query.Contains(Pack)
                        ? "[{\"index\":0,\"name\":\"Show.S01/Show.S01E02.mkv\",\"size\":50,\"progress\":0,\"priority\":1,\"piece_range\":[0,3]},{\"index\":1,\"name\":\"Show.S01/Show.S01E01.mkv\",\"size\":50,\"progress\":1,\"priority\":1,\"piece_range\":[3,6]},{\"index\":2,\"name\":\"Show.S01/notes.txt\",\"size\":1,\"progress\":1,\"priority\":1,\"piece_range\":[6,6]},{\"index\":3,\"name\":\"Show.S01/extra.mkv\",\"size\":10,\"progress\":0,\"priority\":0,\"piece_range\":[6,6]}]"
                        : $"[{{\"index\":0,\"name\":\"{_movieName}\",\"size\":100,\"progress\":0.4,\"priority\":{_moviePriority},\"piece_range\":[0,6]}}]"),
                    "/api/v2/torrents/properties" => Ok("{\"piece_size\":16}"),
                    "/api/v2/torrents/pieceStates" => Ok(JsonSerializer.Serialize(_pieces(Interlocked.Increment(ref _pieceCalls)))),
                    "/api/v2/app/version" => Ok("v5.2.3"),
                    _ when request.Method == HttpMethod.Post => Ok(""),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            case "radarr":
                Assert.Equal("radarr-key", request.Headers.GetValues("X-Api-Key").Single());
                return Ok($"{{\"records\":[{Record(Movie, "movie", "{\"title\":\"Test Film\",\"year\":2026,\"tmdbId\":11,\"images\":[{\"coverType\":\"poster\",\"remoteUrl\":\"https://image.tmdb.org/t/p/original/a.jpg\"}]}", "Test.Film.2026.FRENCH.1080p")},{Record(Other, "movie", "{\"title\":\"Autre\",\"year\":2025,\"tmdbId\":22}", "Autre.2025.MULTi")},{{\"downloadId\":\"zzz\",\"protocol\":\"usenet\",\"movie\":{{\"title\":\"Usenet\"}}}}]}}");
            case "sonarr":
                Assert.Equal("sonarr-key", request.Headers.GetValues("X-Api-Key").Single());
                const string show = "{\"title\":\"Test Show\",\"year\":2024,\"tvdbId\":33,\"tmdbId\":44}";
                return Ok($"{{\"records\":[{Record(Pack, "series", show, "Test.Show.S01.MULTi", "{\"seasonNumber\":1,\"episodeNumber\":1,\"title\":\"Pilot\"}")},{Record(Pack, "series", show, "Test.Show.S01.MULTi", "{\"seasonNumber\":1,\"episodeNumber\":2,\"title\":\"Second\"}")}]}}");
            case "seerr":
                Assert.Equal("seerr-key", request.Headers.GetValues("X-Api-Key").Single());
                return Ok("{\"results\":[{\"type\":\"movie\",\"media\":{\"tmdbId\":11},\"requestedBy\":{\"jellyfinUserId\":\"11111111222233334444555555555555\"}},{\"type\":\"tv\",\"media\":{\"tmdbId\":44,\"tvdbId\":33},\"requestedBy\":{\"jellyfinUserId\":\"11111111222233334444555555555555\"}},{\"type\":\"movie\",\"media\":{\"tmdbId\":22},\"requestedBy\":{\"jellyfinUserId\":\"99999999999999999999999999999999\"}}]}");
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private string Torrents() =>
        $"[{{\"hash\":\"{Movie.ToUpperInvariant()}\",\"name\":\"Test.Film\",\"state\":\"downloading\",\"progress\":0.4,\"dlspeed\":2097152,\"eta\":200,\"save_path\":\"{_directory.Replace("\\", "\\\\")}\",\"download_path\":\"\",\"content_path\":\"/x/film.mkv\",\"seq_dl\":{Lower(_sequential)},\"f_l_piece_prio\":{Lower(_firstLast)}}}," +
        $"{{\"hash\":\"{Other}\",\"name\":\"Autre\",\"state\":\"queuedDL\",\"progress\":0,\"dlspeed\":0,\"eta\":8640000,\"save_path\":\"/x\",\"content_path\":\"/x/autre.mkv\",\"seq_dl\":true,\"f_l_piece_prio\":false}}," +
        $"{{\"hash\":\"{Pack}\",\"name\":\"Show.S01\",\"state\":\"downloading\",\"progress\":0.5,\"dlspeed\":1048576,\"eta\":60,\"save_path\":\"/x\",\"content_path\":\"/x/Show.S01\",\"seq_dl\":true,\"f_l_piece_prio\":true}}]";

    private static string Record(string hash, string kind, string media, string release, string? episode = null) =>
        $"{{\"downloadId\":\"{hash.ToUpperInvariant()}\",\"protocol\":\"torrent\",\"title\":\"{release}\",\"{kind}\":{media}{(episode is null ? "" : ",\"episode\":" + episode)}}}";
    private static string Lower(bool value) => value ? "true" : "false";
    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public class ProbeProxy : DispatchProxy
    {
        public bool Probed;
        protected override object? Invoke(MethodInfo? method, object?[]? args) { Assert.Equal("AddMediaInfoWithProbe", method!.Name); Probed = true; return Task.CompletedTask; }
    }
    public class NoUsers : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Without Seerr, nobody needs a user lookup.");
    }
    private sealed class Factory(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(handle));
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}
