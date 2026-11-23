using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.TorrServer;

public static class Probe
{
    /// <summary>
    /// Probes a remote source without Jellyfin's probe cache. With a cache key, Jellyfin 12.4 opens
    /// &lt;cache&gt;/mediainfo/&lt;key&gt;.json before probing and fails when it does not exist yet, so every first playback of
    /// a channel item ended with no source. Jellyfin already keeps a channel item's sources for five minutes.
    /// </summary>
    public static Task Run(IMediaSourceManager media, MediaSourceInfo source, CancellationToken ct) => media.AddMediaInfoWithProbe(source, false, null!, false, false, ct);

    /// <summary>
    /// A media source id for a channel item. Jellyfin 12.4 parses it as a Guid when it builds an HLS playlist, so any
    /// other form makes transcoding and remuxing fail with HTTP 500. The Guid is derived from the item id, hence stable.
    /// </summary>
    public static string SourceId(string itemId) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(itemId))).ToString("N");
}
