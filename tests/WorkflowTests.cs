using System.Net;
using System.Reflection;
using System.Text;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Xunit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Plugin.TorrServer;

public sealed class WorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ts-test-" + Guid.NewGuid());
    private readonly PluginConfiguration _config = new() { TmdbApiKey = "tmdb-test", ProwlarrUrl = "http://prowlarr/base", ProwlarrApiKey = "private-key", TorrServerUrl = "http://torrent/base", TorrServerUsername = "user", TorrServerPassword = "password" };
    private readonly List<string> _requests = [];
    private readonly List<string> _bodies = [];
    private Services Make(Func<HttpRequestMessage, string>? handle = null) => new(new Factory(async request =>
    {
        var url = request.RequestUri!.AbsoluteUri; _requests.Add(url);
        if (url.Contains("prowlarr")) Assert.Equal("private-key", request.Headers.GetValues("X-Api-Key").Single());
        if (request.RequestUri!.Host == "torrent") Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
        if (request.RequestUri!.Host == "external") Assert.False(request.Headers.Contains("X-Api-Key"));
        if (request.Content is not null) _bodies.Add(await request.Content.ReadAsStringAsync());
        var body = handle?.Invoke(request) ?? (url.Contains("themoviedb") ? "{\"name\":\"Test Show\",\"original_name\":\"Test Show\"}" : url.Contains("prowlarr") ? "[{\"protocol\":\"torrent\",\"title\":\"Test.Show.S02.Complete\",\"magnetUrl\":\"magnet:?xt=urn:btih:abc\",\"seeders\":23,\"size\":1024,\"indexer\":\"Fixture\"}]" : "{\"hash\":\"abc\",\"file_stats\":[{\"id\":1,\"path\":\"Show.S02E03.mkv\",\"length\":1234},{\"id\":2,\"path\":\"Show.S02E04.mkv\",\"length\":4567},{\"id\":3,\"path\":\"readme.txt\",\"length\":3}]}");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }), () => _config);

    [Fact]
    public async Task PackSelectionPersistsAndPlaysChosenFile()
    {
        var services = Make(); var store = new SelectionStore(() => _directory);
        var media = DispatchProxy.Create<IMediaSourceManager, ProbeProxy>();
        var tickets = new PlaybackTickets();
        var channel = new TmdbChannel(services, store, media, tickets);
        var scopes = await channel.GetChannelItems(new() { FolderId = "episode:42:2:3" }, default);
        Assert.Equal(3, scopes.Items.Count);
        Assert.Empty(_requests);
        var results = await channel.GetChannelItems(new() { FolderId = scopes.Items[1].Id }, default);
        Assert.Single(results.Items);
        Assert.Contains("Test%20Show%20S02", _requests.Single(x => x.Contains("prowlarr")));
        var files = await channel.GetChannelItems(new() { FolderId = results.Items[0].Id }, default);
        Assert.Equal(2, files.Items.Count);
        Assert.Contains(_bodies, x => x.Contains("save_to_db"));
        var restarted = new TmdbChannel(services, new SelectionStore(() => _directory), media, tickets);
        var source = Assert.Single(await restarted.GetChannelItemMediaInfo(files.Items[1].Id, default));
        Assert.Contains("index=2", tickets.Get(new Uri(source.Path).Segments.Last()));
        Assert.Contains("link=abc", tickets.Get(new Uri(source.Path).Segments.Last()));
        Assert.Empty(source.RequiredHttpHeaders);
        Assert.Equal("Show.S02E04.mkv", source.Name);
        Assert.False(source.SupportsDirectPlay);
        Assert.True(source.SupportsTranscoding);
        Assert.True(((ProbeProxy)(object)media).Probed);
        Assert.DoesNotContain("private-key", files.Items[1].Id);
    }
    [Fact]
    public async Task BrowsingCatalogueNeverStartsTorrent()
    {
        var services = Make(_ => "{\"results\":[{\"id\":7,\"title\":\"Film\",\"poster_path\":\"/poster.jpg\"}],\"total_pages\":2}");
        var channel = new TmdbChannel(services, new SelectionStore(() => _directory), DispatchProxy.Create<IMediaSourceManager, ProbeProxy>(), new PlaybackTickets());
        var page = await channel.GetChannelItems(new() { FolderId = "popular:movie:1" }, default);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("search:movie:7:0:0", page.Items[0].Id);
        Assert.Equal(ChannelItemType.Folder, page.Items[0].Type);
        Assert.Equal("popular:movie:2", page.Items[1].Id);
        Assert.All(_requests, x => Assert.Contains("themoviedb.org", x));
    }
    [Theory]
    [InlineData("Show.S02E03.mkv", 2, 3, true)]
    [InlineData("Show.2x03.mkv", 2, 3, true)]
    [InlineData("Show.S02E030.mkv", 2, 3, false)]
    [InlineData("Show.S12E03.mkv", 2, 3, false)]
    public void EpisodeMatching(string path, int season, int episode, bool expected) => Assert.Equal(expected, TmdbChannel.MatchesEpisode(path, season, episode));
    [Fact]
    public async Task NullSeedersAndUsenetAreHandled()
    {
        var results = await Make(_ => "[{\"protocol\":\"usenet\",\"downloadUrl\":\"http://bad\"},{\"protocol\":\"torrent\",\"seeders\":null,\"size\":null,\"downloadUrl\":\"http://file\"}]").Search("test", default);
        Assert.Equal(0, Assert.Single(results).Seeders);
    }
    [Fact]
    public async Task StoreRejectsTraversal() => await Assert.ThrowsAsync<ArgumentException>(() => new SelectionStore(() => _directory).Load("../secrets", default));
    [Fact]
    public async Task TorrentFileIsUploadedAndCredentialsStayAtProwlarr()
    {
        var services = Make(request => request.RequestUri!.Host == "external" ? "d4:infodee" : "{\"hash\":\"abc\",\"file_stats\":[{\"id\":1,\"path\":\"film.mp4\",\"length\":42}]}");
        var status = await services.Prepare(new("x", "Film", "http://external/source.torrent", 1, 4, "fixture"), default);
        Assert.Equal("abc", status.Hash);
        Assert.Contains(_bodies, x => x.Contains("source.torrent"));
    }
    [Fact]
    public async Task PlaybackRelayPreservesRangeAndHidesCredentials()
    {
        var tickets = new PlaybackTickets(); var token = tickets.Issue("http://torrent/base/stream/film.mp4?link=abc&index=2&play");
        var http = new Factory(request =>
        {
            Assert.Equal("bytes=0-3", request.Headers.Range!.ToString());
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2, 3, 4]) };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 3, 100);
            return Task.FromResult(response);
        });
        var context = new DefaultHttpContext(); context.Request.Method = "GET"; context.Request.Headers.Range = "bytes=0-3"; context.Response.Body = new MemoryStream();
        var controller = new PlaybackController(tickets, Make(), http) { ControllerContext = new ControllerContext { HttpContext = context } };
        await controller.Play(token, default);
        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal("bytes 0-3/100", context.Response.Headers.ContentRange);
        Assert.Equal(4, context.Response.Body.Length);
        Assert.False(context.Response.Headers.ContainsKey("Authorization"));
    }
    [Fact]
    public async Task UnknownPlaybackTicketDoesNotReachService()
    {
        var controller = new PlaybackController(new PlaybackTickets(), Make(), new Factory(_ => throw new Exception("Must not send"))) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        await controller.Play("unknown", default);
        Assert.Equal(404, controller.Response.StatusCode);
    }
    [Fact]
    public async Task MetadataWaitHonoursCancellation()
    {
        using var cancel = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Make(_ => "{\"hash\":\"abc\",\"file_stats\":[]}").Prepare(new("x", "Film", "magnet:?xt=urn:btih:abc", 1, 4, "fixture"), cancel.Token));
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    public class ProbeProxy : DispatchProxy
    {
        public bool Probed;
        protected override object? Invoke(MethodInfo? method, object?[]? args) { Assert.Equal("AddMediaInfoWithProbe", method!.Name); Probed = true; return Task.CompletedTask; }
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
