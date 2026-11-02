using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorrServer;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("TorrServerPlugin/connections")]
public sealed class CatalogController(Services services, IHttpClientFactory http) : ControllerBase
{
    [HttpPost]
    public async Task<IReadOnlyDictionary<string, string>> Test(CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        result["TMDB"] = await Check(async () => { await services.Tmdb("configuration", ct); });
        result["Prowlarr"] = await Check(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Services.Endpoint(services.Config.ProwlarrUrl, "api/v1/system/status"));
            request.Headers.Add("X-Api-Key", services.Config.ProwlarrApiKey);
            using var response = await http.CreateClient("TorrServerNoRedirect").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
        });
        result["TorrServer"] = await Check(async () =>
        {
            using var request = services.TorrRequest("torrents"); request.Content = JsonContent.Create(new { action = "list" });
            using var response = await http.CreateClient("TorrServerNoRedirect").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
        });
        return result;
    }
    private static async Task<string> Check(Func<Task> operation)
    {
        try { await operation(); return "Connexion OK"; }
        catch (InvalidOperationException ex) { return ex.Message; }
        catch (HttpRequestException) { return "Connexion impossible. Vérifiez l'adresse, le réseau et le certificat."; }
        catch (OperationCanceledException) { return "Délai de connexion dépassé."; }
        catch (System.Text.Json.JsonException) { return "Réponse JSON invalide."; }
    }
}
