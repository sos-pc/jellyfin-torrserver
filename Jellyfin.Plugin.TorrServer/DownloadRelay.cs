using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Grants access to one file of one download, without disclosing paths or qBittorrent credentials.</summary>
public sealed class DownloadTickets
{
    private readonly ConcurrentDictionary<string, (string Hash, int FileIndex, DateTime Expires)> _tickets = new();
    public string Issue(string hash, int fileIndex)
    {
        foreach (var pair in _tickets.Where(x => x.Value.Expires < DateTime.UtcNow)) _tickets.TryRemove(pair.Key, out _);
        if (_tickets.Count >= 2048) throw new InvalidOperationException("Trop de sessions de lecture actives.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _tickets[token] = (QbitClient.NormalizeHash(hash), fileIndex, DateTime.UtcNow.AddHours(24));
        return token;
    }
    public (string Hash, int FileIndex)? Get(string token) => _tickets.TryGetValue(token, out var value) && value.Expires > DateTime.UtcNow ? (value.Hash, value.FileIndex) : null;
}

/// <summary>An HTTP byte range resolved against a file size, end included.</summary>
public readonly record struct ByteRange(long Start, long End, bool Partial)
{
    /// <summary>Reads the first range of a Range header. Null when it cannot be satisfied (HTTP 416).</summary>
    public static ByteRange? Parse(string? header, long size)
    {
        if (string.IsNullOrWhiteSpace(header)) return size > 0 ? new ByteRange(0, size - 1, false) : null;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
        var spec = header[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return null;
        var left = spec[..dash].Trim();
        var right = spec[(dash + 1)..].Trim();
        long start, end;
        if (left.Length == 0)
        {
            if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return null;
            start = Math.Max(0, size - suffix);
            end = size - 1;
        }
        else
        {
            if (!long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out start)) return null;
            end = right.Length == 0 ? size - 1 : long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var e) ? Math.Min(e, size - 1) : -1;
        }
        return start < size && end >= start ? new ByteRange(start, end, true) : null;
    }
}

/// <summary>
/// Serves a file that qBittorrent is still downloading. Each block is sent once the pieces that hold it are downloaded
/// and checked; until then the request waits instead of failing, as a player waits for a slow disk.
/// </summary>
[ApiController]
[Route("TorrServerPlugin/download")]
public sealed class DownloadRelayController(DownloadTickets tickets, QbitClient qbit) : ControllerBase
{
    private const int BlockSize = 256 * 1024;

    [AllowAnonymous]
    [HttpGet("{ticket}")]
    [HttpHead("{ticket}")]
    public async Task Play(string ticket, CancellationToken ct)
    {
        var target = tickets.Get(ticket);
        if (target is null) { Response.StatusCode = 404; return; }
        var file = await qbit.Locate(target.Value.Hash, target.Value.FileIndex, ct);
        var size = file.File.Size;
        var range = ByteRange.Parse(Request.Headers.Range.ToString(), size);
        if (range is null)
        {
            Response.StatusCode = 416;
            Response.Headers.ContentRange = $"bytes */{size}";
            return;
        }
        var (start, end, partial) = range.Value;
        Response.StatusCode = partial ? 206 : 200;
        Response.Headers.AcceptRanges = "bytes";
        Response.ContentType = ContentType(file.File.Name);
        Response.ContentLength = end - start + 1;
        if (partial) Response.Headers.ContentRange = $"bytes {start}-{end}/{size}";
        if (HttpMethods.IsHead(Request.Method)) return;

        var buffer = new byte[BlockSize];
        var position = start;
        FileStream? stream = null;
        try
        {
            while (position <= end)
            {
                var blockEnd = Math.Min(end, position + BlockSize - 1);
                await qbit.WaitForBytes(file, position, blockEnd, ct);
                // qBittorrent creates the file with its first piece; once its pieces are checked, the bytes are on disk.
                stream ??= new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BlockSize, useAsync: true);
                stream.Seek(position, SeekOrigin.Begin);
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)(blockEnd - position + 1)), ct);
                if (read == 0) { await Task.Delay(500, ct); continue; }
                await Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                position += read;
            }
        }
        // A player that seeks or stops closes its request: that is the normal end of a read, not an error.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (stream is not null) await stream.DisposeAsync();
        }
    }

    public static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".mkv" => "video/x-matroska",
        ".mp4" or ".m4v" => "video/mp4",
        ".avi" => "video/x-msvideo",
        ".ts" or ".m2ts" => "video/mp2t",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        _ => "application/octet-stream",
    };
}
