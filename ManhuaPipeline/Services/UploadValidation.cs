namespace ManhuaPipeline.Services;

public static class UploadValidation
{
    public const long MaxProfileImageBytes = 20L * 1024 * 1024;
    public const long MaxLibraryImageBytes = 50L * 1024 * 1024;
    public const long MaxVideoBytes = 500L * 1024 * 1024;
    public const long MaxReferenceVideoBytes = 300L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".webm", ".mkv", ".avi"
    };

    public static bool TryValidateImage(IFormFile? file, long maxBytes, out string extension, out string error)
    {
        extension = NormalizeExtension(file?.FileName);
        if (!TryValidateCommon(file, maxBytes, ImageExtensions, extension, out error))
            return false;

        var header = ReadHeader(file!, 8192);
        var valid = extension switch
        {
            ".jpg" or ".jpeg" => IsValidJpeg(header),
            ".png" => IsValidPng(header),
            ".gif" => IsValidGif(header),
            ".webp" => IsValidWebP(header, file!.Length),
            _ => false
        };

        if (valid) return true;
        error = "文件内容与图片扩展名不匹配";
        return false;
    }

    public static bool TryValidateVideo(IFormFile? file, long maxBytes, out string extension, out string error)
    {
        extension = NormalizeExtension(file?.FileName);
        if (!TryValidateCommon(file, maxBytes, VideoExtensions, extension, out error))
            return false;

        var header = ReadHeader(file!, 8192);
        var valid = extension switch
        {
            ".mp4" or ".mov" => IsValidMp4(header),
            ".webm" or ".mkv" => IsValidWebM(header),
            ".avi" => IsValidAvi(header, file!.Length),
            _ => false
        };

        if (valid) return true;
        error = "文件内容与视频扩展名不匹配";
        return false;
    }

    private static bool TryValidateCommon(
        IFormFile? file,
        long maxBytes,
        HashSet<string> allowedExtensions,
        string extension,
        out string error)
    {
        if (file == null || file.Length <= 0)
        {
            error = "请选择文件";
            return false;
        }

        if (file.Length > maxBytes)
        {
            error = $"文件不能超过 {maxBytes / 1024 / 1024} MB";
            return false;
        }

        if (!allowedExtensions.Contains(extension))
        {
            error = allowedExtensions == ImageExtensions
                ? "仅支持 jpg/jpeg/png/webp/gif 格式"
                : "仅支持 mp4/mov/webm/mkv/avi 格式";
            return false;
        }

        error = "";
        return true;
    }

    private static string NormalizeExtension(string? fileName) =>
        Path.GetExtension(fileName ?? "").ToLowerInvariant();

    private static byte[] ReadHeader(IFormFile file, int length)
    {
        var header = new byte[length];
        using var stream = file.OpenReadStream();
        var total = 0;
        while (total < header.Length)
        {
            var read = stream.Read(header, total, header.Length - total);
            if (read == 0) break;
            total += read;
        }

        return total == header.Length ? header : header[..total];
    }

    private static bool IsValidJpeg(ReadOnlySpan<byte> h)
    {
        if (!StartsWith(h, [0xFF, 0xD8, 0xFF])) return false;

        var i = 2;
        while (i + 3 < h.Length)
        {
            if (h[i] != 0xFF) return false;
            var marker = h[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker == 0x01 || marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            if (marker == 0xD9) return false;
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (i + 9 > h.Length) return false;
                return ReadBigEndianUInt16(h.Slice(i + 5, 2)) > 0
                    && ReadBigEndianUInt16(h.Slice(i + 7, 2)) > 0;
            }
            if (i + 4 > h.Length) return false;
            var segmentLength = ReadBigEndianUInt16(h.Slice(i + 2, 2));
            if (segmentLength < 2) return false;
            i += 2 + segmentLength;
        }
        return false;
    }

    private static bool IsValidPng(ReadOnlySpan<byte> h)
    {
        if (!StartsWith(h, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) || h.Length < 24) return false;
        return ReadBigEndianUInt32(h.Slice(8, 4)) == 13
            && MatchesAt(h, 12, "IHDR"u8)
            && ReadBigEndianUInt32(h.Slice(16, 4)) > 0
            && ReadBigEndianUInt32(h.Slice(20, 4)) > 0;
    }

    private static bool IsValidGif(ReadOnlySpan<byte> h)
    {
        if (!(StartsWith(h, "GIF87a"u8) || StartsWith(h, "GIF89a"u8)) || h.Length < 10) return false;
        return ReadLittleEndianUInt16(h.Slice(6, 2)) > 0 && ReadLittleEndianUInt16(h.Slice(8, 2)) > 0;
    }

    private static bool IsValidWebP(ReadOnlySpan<byte> h, long fileLength)
    {
        return h.Length >= 12
            && fileLength >= 12
            && StartsWith(h, "RIFF"u8)
            && MatchesAt(h, 8, "WEBP"u8)
            && (ulong)ReadLittleEndianUInt32(h.Slice(4, 4)) == (ulong)(fileLength - 8);
    }

    private static bool IsValidMp4(ReadOnlySpan<byte> h)
    {
        return h.Length >= 16 && MatchesAt(h, 4, "ftyp"u8) && ReadBigEndianUInt32(h.Slice(0, 4)) >= 8;
    }

    private static bool IsValidWebM(ReadOnlySpan<byte> h)
    {
        return StartsWith(h, [0x1A, 0x45, 0xDF, 0xA3])
            && (ContainsAscii(h, "webm"u8) || ContainsAscii(h, "matroska"u8));
    }

    private static bool IsValidAvi(ReadOnlySpan<byte> h, long fileLength)
    {
        return h.Length >= 12
            && fileLength >= 12
            && StartsWith(h, "RIFF"u8)
            && MatchesAt(h, 8, "AVI "u8)
            && (ulong)ReadLittleEndianUInt32(h.Slice(4, 4)) == (ulong)(fileLength - 8);
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> value, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || needle.Length > value.Length) return false;
        for (var i = 0; i <= value.Length - needle.Length; i++)
        {
            if (value.Slice(i, needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }

    private static ushort ReadBigEndianUInt16(ReadOnlySpan<byte> data) =>
        (ushort)((data[0] << 8) | data[1]);

    private static ushort ReadLittleEndianUInt16(ReadOnlySpan<byte> data) =>
        (ushort)((data[1] << 8) | data[0]);

    private static uint ReadBigEndianUInt32(ReadOnlySpan<byte> data) =>
        (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);

    private static uint ReadLittleEndianUInt32(ReadOnlySpan<byte> data) =>
        (uint)((data[3] << 24) | (data[2] << 16) | (data[1] << 8) | data[0]);

    private static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expected) =>
        value.Length >= expected.Length && value[..expected.Length].SequenceEqual(expected);

    private static bool MatchesAt(ReadOnlySpan<byte> value, int offset, ReadOnlySpan<byte> expected) =>
        value.Length >= offset + expected.Length && value.Slice(offset, expected.Length).SequenceEqual(expected);
}
