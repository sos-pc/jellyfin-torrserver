using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Only grants access to one selected file, without disclosing service credentials.</summary>
public sealed class PlaybackTickets
{
    private readonly ConcurrentDictionary<string, (string Url, DateTime Expires)> _tickets = new();
    public string Issue(string url)
    {
        foreach (var pair in _tickets.Where(x => x.Value.Expires < DateTime.UtcNow)) _tickets.TryRemove(pair.Key, out _);
        if (_tickets.Count >= 2048) throw new InvalidOperationException("Trop de sessions de lecture actives.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _tickets[token] = (url, DateTime.UtcNow.AddHours(24));
        return token;
    }
    public string? Get(string token) => _tickets.TryGetValue(token, out var value) && value.Expires > DateTime.UtcNow ? value.Url : null;
}

[ApiController]
[Route("TorrServerPlugin/play")]
public sealed class PlaybackController(PlaybackTickets tickets, Services services, IHttpClientFactory http) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("{ticket}")]
    [HttpHead("{ticket}")]
    public async Task Play(string ticket, CancellationToken ct)
    {
        var url = tickets.Get(ticket);
        if (url is null) { Response.StatusCode = 404; return; }
        using var request = services.TorrRequest("");
        request.RequestUri = new Uri(url);
        request.Method = HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get;
        foreach (var header in new[] { "Range", "If-Range" }) if (Request.Headers.TryGetValue(header, out var value)) request.Headers.TryAddWithoutValidation(header, value.ToArray());
        using var upstream = await http.CreateClient("TorrServerStream").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Response.StatusCode = (int)upstream.StatusCode;
        foreach (var header in new[] { "Accept-Ranges", "Content-Range", "Content-Length", "Content-Type", "Last-Modified", "ETag" })
        {
            if (upstream.Headers.TryGetValues(header, out var values) || upstream.Content.Headers.TryGetValues(header, out values)) Response.Headers[header] = string.Join(", ", values);
        }
        if (!HttpMethods.IsHead(Request.Method)) await upstream.Content.CopyToAsync(Response.Body, ct);
    }
}
