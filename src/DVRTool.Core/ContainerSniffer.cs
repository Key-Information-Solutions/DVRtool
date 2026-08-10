namespace DVRTool.Core;

/// <summary>Media container of a downloaded export, as determined from its bytes.</summary>
public enum MediaContainer
{
    Unknown,
    /// <summary>MPEG-2 program stream — what Hikvision ISAPI downloads actually are.</summary>
    MpegProgramStream,
    Mp4,
    Matroska,
    /// <summary>Dahua's proprietary DHAV stream (the ".dav" files).</summary>
    Dhav,
}

/// <summary>
/// Identifies an export's real container from its leading bytes.
/// <para>
/// Hikvision NVRs label ISAPI downloads ".mp4" but return an MPEG program stream
/// behind a fixed-size private header (observed: 64 bytes, magic "IMKH" at 0x18,
/// the pack header at 0x40). VLC sniffs content and plays them; most other players
/// trust the extension and refuse. Naming has to follow the bytes, not the vendor:
/// other firmware may legitimately return a real MP4.
/// </para>
/// </summary>
public static class ContainerSniffer
{
    /// <summary>Bytes read from the front of a file to identify it.</summary>
    public const int HeaderSize = 1024;

    private static ReadOnlySpan<byte> PackHeader => [0x00, 0x00, 0x01, 0xBA];
    private static ReadOnlySpan<byte> EbmlHeader => [0x1A, 0x45, 0xDF, 0xA3];

    public static MediaContainer Sniff(ReadOnlySpan<byte> header)
    {
        // ISO-BMFF and EBML magics are checked first: their payloads can contain a
        // byte run that looks like an MPEG pack header.
        if (header.Length >= 8 && header[4..8].SequenceEqual("ftyp"u8))
            return MediaContainer.Mp4;
        if (header.Length >= 4 && header[..4].SequenceEqual(EbmlHeader))
            return MediaContainer.Matroska;
        if (header.Length >= 4 && header[..4].SequenceEqual("DHAV"u8))
            return MediaContainer.Dhav;
        if (header.IndexOf(PackHeader) >= 0)
            return MediaContainer.MpegProgramStream;
        return MediaContainer.Unknown;
    }

    public static MediaContainer SniffFile(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> buffer = stackalloc byte[HeaderSize];
        int read = file.ReadAtLeast(buffer, HeaderSize, throwOnEndOfStream: false);
        return Sniff(buffer[..read]);
    }

    /// <summary>File extension for a sniffed container; null when it is unrecognized.</summary>
    public static string? ExtensionFor(MediaContainer container) => container switch
    {
        MediaContainer.MpegProgramStream => ".mpg",
        MediaContainer.Mp4 => ".mp4",
        MediaContainer.Matroska => ".mkv",
        MediaContainer.Dhav => ".dav",
        _ => null,
    };

    /// <summary>Container a file name claims to hold; Unknown when the extension says nothing.</summary>
    public static MediaContainer ContainerForExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mpg" or ".mpeg" or ".ps" or ".vob" or ".m2p" => MediaContainer.MpegProgramStream,
            ".mp4" or ".m4v" or ".mov" => MediaContainer.Mp4,
            ".mkv" => MediaContainer.Matroska,
            ".dav" => MediaContainer.Dhav,
            _ => MediaContainer.Unknown,
        };

    /// <summary>
    /// True when <paramref name="path"/>'s extension actively contradicts the sniffed
    /// container. An unrecognized extension or an unrecognized container is not a
    /// contradiction — only a confident mismatch is worth warning about.
    /// </summary>
    public static bool ExtensionContradicts(string path, MediaContainer sniffed)
    {
        if (sniffed == MediaContainer.Unknown)
            return false;
        var claimed = ContainerForExtension(path);
        return claimed != MediaContainer.Unknown && claimed != sniffed;
    }

    public static string DisplayName(MediaContainer container) => container switch
    {
        MediaContainer.MpegProgramStream => "MPEG program stream",
        MediaContainer.Mp4 => "MP4",
        MediaContainer.Matroska => "Matroska",
        MediaContainer.Dhav => "DHAV",
        _ => "unrecognized container",
    };
}
