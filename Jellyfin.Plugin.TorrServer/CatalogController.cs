using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorrServer;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("TorrServerPlugin")]
public sealed class CatalogController : ControllerBase
{
    private readonly IHttpClientFactory _clients;
    public CatalogController(IHttpClientFactory clients) => _clients = clients;

    [HttpGet("catalog/{kind}")]
    public async Task<IActionResult> Catalog(string kind, [FromQuery] string? query, [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        if (kind is not ("movie" or "tv") || page is < 1 or > 500 || query?.Length > 200) return BadRequest();
        var config = Plugin.Instance?.Configuration;
        if (string.IsNullOrWhiteSpace(config?.TmdbApiKey)) return Problem("La clé TMDB n'est pas configurée.");
        var path = string.IsNullOrWhiteSpace(query) ? $"{kind}/popular" : $"search/{kind}";
        var url = $"https://api.themoviedb.org/3/{path}?api_key={Uri.EscapeDataString(config.TmdbApiKey)}&language=fr-FR&page={page}";
        if (!string.IsNullOrWhiteSpace(query)) url += "&query=" + Uri.EscapeDataString(query);
        using var response = await _clients.CreateClient().GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return StatusCode(502, "TMDB a refusé la requête.");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return Ok(json.RootElement.Clone());
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) return BadRequest();
        var config = Plugin.Instance?.Configuration;
        if (!Uri.TryCreate(config?.ProwlarrUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(config.ProwlarrApiKey)) return Problem("Prowlarr n'est pas configuré.");
        var url = config.ProwlarrUrl.TrimEnd('/') + "/api/v1/search?type=search&query=" + Uri.EscapeDataString(query);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Api-Key", config.ProwlarrApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _clients.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return StatusCode(502, "Prowlarr a refusé la requête.");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return Ok(json.RootElement.Clone());
    }
}
