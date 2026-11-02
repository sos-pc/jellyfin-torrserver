using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TorrServer;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddHttpClient("TorrServerNoRedirect", c => c.Timeout = TimeSpan.FromMinutes(3))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddHttpClient("TorrServerStream", c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton<PlaybackTickets>();
        services.AddSingleton(sp => new Services(sp.GetRequiredService<IHttpClientFactory>(), () => Plugin.Instance!.Configuration));
        services.AddSingleton(new SelectionStore(() => Path.Combine(Plugin.Instance!.DataFolderPath, "selections")));
        services.AddSingleton<IChannel, TmdbChannel>();
    }
}
