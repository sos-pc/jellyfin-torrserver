using System.Buffers.Binary;
using System.Text;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>What a player needs from a video file that is still downloading, depending on its container.</summary>
public static class Containers
{
    /// <summary>Bytes needed at the start before playback: headers, track list, and the index of a « fast start » MP4.</summary>
    public static long HeadSize(long size) => Math.Min(4L * 1024 * 1024, Math.Max(1, size / 2));

    /// <summary>The end of a file, where Matroska, AVI and MPEG-TS keep only optional data (Cues, Tags, idx1).</summary>
    public static long TailSize(long size) => Math.Min(32L * 1024 * 1024, size / 8);

    /// <summary>
    /// Whether a player can open the file without its end. Matroska and AVI keep their index there, but ffmpeg plays without
    /// it; an MP4 cannot play without its index (moov), which only a « fast start » file keeps at the start.
    /// </summary>
    public static bool TailIsOptional(string name) => Path.GetExtension(name).ToLowerInvariant() is ".mkv" or ".webm" or ".avi" or ".ts" or ".m2ts";

    /// <summary>Whether playback must wait for the end of the file, given the bytes of its start.</summary>
    public static bool NeedsTail(string name, ReadOnlySpan<byte> head) => !TailIsOptional(name) && !Mp4IndexFirst(head);

    /// <summary>Walks the top-level boxes of an MP4 start: true when moov comes before mdat.</summary>
    public static bool Mp4IndexFirst(ReadOnlySpan<byte> head)
    {
        long position = 0;
        while (position + 8 <= head.Length)
        {
            var box = head[(int)position..];
            long size = BinaryPrimitives.ReadUInt32BigEndian(box);
            var type = Encoding.ASCII.GetString(box.Slice(4, 4));
            if (type == "moov") return true;
            if (type == "mdat") return false;
            if (size == 1)
            {
                if (box.Length < 16) return false;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(box.Slice(8, 8));
            }
            // 0 means « up to the end of the file », and anything under a box header is not an MP4 box.
            if (size < 8) return false;
            position += size;
        }
        return false;
    }

    public static async Task<byte[]> ReadStart(string path, int count, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) break;
            read += n;
        }
        return buffer[..read];
    }
}
