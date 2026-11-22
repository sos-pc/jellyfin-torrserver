using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
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
        services.AddHttpClient("TorrServerQbit", c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        services.AddSingleton<PlaybackTickets>();
        services.AddSingleton<DownloadTickets>();
        services.AddSingleton(sp => new Services(sp.GetRequiredService<IHttpClientFactory>(), () => Plugin.Instance!.Configuration));
        services.AddSingleton(sp => new QbitClient(sp.GetRequiredService<IHttpClientFactory>(), () => Plugin.Instance!.Configuration));
        services.AddSingleton(sp => new DownloadCatalog(sp.GetRequiredService<IHttpClientFactory>(), () => Plugin.Instance!.Configuration, sp.GetRequiredService<QbitClient>()));
        services.AddSingleton(new SelectionStore(() => Path.Combine(Plugin.Instance!.DataFolderPath, "selections")));
        services.AddSingleton<IChannel>(sp => new TmdbChannel(sp.GetRequiredService<Services>(), sp.GetRequiredService<SelectionStore>(), sp.GetRequiredService<IMediaSourceManager>(), sp.GetRequiredService<PlaybackTickets>()));
        services.AddSingleton<IChannel>(sp => new DownloadsChannel(sp.GetRequiredService<DownloadCatalog>(), sp.GetRequiredService<DownloadTickets>(), sp.GetRequiredService<IMediaSourceManager>(), sp.GetRequiredService<IUserManager>(), () => Plugin.Instance!.Configuration));
    }
}
