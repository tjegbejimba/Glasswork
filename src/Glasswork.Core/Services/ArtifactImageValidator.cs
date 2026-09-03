using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public sealed record ArtifactImageValidation(
    bool IsValid,
    byte[]? Content,
    string? ContentType,
    int Width,
    int Height,
    string? Error);

/// <summary>Validates untrusted image Artifacts before a browser/native surface receives bytes.</summary>
public static class ArtifactImageValidator
{
    public static ArtifactImageValidation Validate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Invalid("Image file is missing.");
            if (info.Length > ArtifactCaps.InlineImageBytes)
                return Invalid($"File exceeds the {ArtifactRow.FormatSize(ArtifactCaps.InlineImageBytes)} inline image limit.");

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".svg") return ValidateSvg(path);

            var content = File.ReadAllBytes(path);
            var dimensions = extension switch
            {
                ".png" => ReadPng(content),
                ".jpg" or ".jpeg" => ReadJpeg(content),
                ".gif" => ReadGif(content),
                ".bmp" => ReadBmp(content),
                ".webp" => ReadWebP(content),
                _ => null,
            };
            if (dimensions is not { } size) return Invalid("Image could not be decoded.");
            var error = ValidateDimensions(size.Width, size.Height);
            if (error is not null) return Invalid(error);

            return new(true, content, ContentType(extension), size.Width, size.Height, null);
        }
        catch (Exception ex)
        {
            return Invalid($"Image could not be decoded: {ex.Message}");
        }
    }

    private static ArtifactImageValidation ValidateSvg(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = ArtifactCaps.InlineImageBytes,
        };
        using var reader = XmlReader.Create(path, settings);
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
            return Invalid("SVG could not be decoded.");

        var width = ParseSvgDimension(root.Attribute("width")?.Value);
        var height = ParseSvgDimension(root.Attribute("height")?.Value);
        if ((width <= 0 || height <= 0) && TryReadViewBox(root.Attribute("viewBox")?.Value, out var viewWidth, out var viewHeight))
        {
            width = viewWidth;
            height = viewHeight;
        }
        var error = ValidateDimensions(width, height);
        if (error is not null) return Invalid(error);

        var blockedElements = new[] { "script", "foreignObject", "iframe", "object", "embed", "audio", "video", "source" };
        foreach (var element in root.Descendants().Where(e => blockedElements.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase)).ToList())
            element.Remove();

        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes().ToList())
            {
                var name = attribute.Name.LocalName;
                var value = attribute.Value;
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("href", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("src", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("srcset", StringComparison.OrdinalIgnoreCase)
                    || (name.Equals("style", StringComparison.OrdinalIgnoreCase) && HasExternalCss(value)))
                {
                    attribute.Remove();
                }
            }
        }
        foreach (var style in root.Descendants().Where(e => e.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (HasExternalCss(style.Value)) style.Remove();
        }

        var sanitized = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        return new(true, sanitized, "image/svg+xml", width, height, null);
    }

    private static bool HasExternalCss(string value) =>
        value.Contains("url(", StringComparison.OrdinalIgnoreCase)
        || value.Contains("@import", StringComparison.OrdinalIgnoreCase)
        || value.Contains("expression(", StringComparison.OrdinalIgnoreCase);

    private static (int Width, int Height)? ReadPng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 45 || !bytes.AsSpan(0, 8).SequenceEqual(signature)
            || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return null;
        var dimensions = (Width: BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)),
            Height: BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        var offset = 8;
        var sawData = false;
        while (offset + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > int.MaxValue || offset + 12L + length > bytes.Length) return null;
            var type = bytes.AsSpan(offset + 4, 4);
            var chunkEnd = checked(offset + (int)length + 8);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(chunkEnd, 4));
            if (Crc32(bytes.AsSpan(offset + 4, checked((int)length + 4))) != expectedCrc) return null;
            if (type.SequenceEqual("IDAT"u8)) sawData = true;
            if (type.SequenceEqual("IEND"u8)) return length == 0 && sawData ? dimensions : null;
            offset += checked((int)length + 12);
        }
        return null;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xffffffffu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static (int Width, int Height)? ReadGif(byte[] bytes)
    {
        if (bytes.Length < 10
            || bytes[^1] != 0x3b
            || (!bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8) && !bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8)))
            return null;
        return (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2)));
    }

    private static (int Width, int Height)? ReadBmp(byte[] bytes)
    {
        if (bytes.Length < 26 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M') return null;
        return (Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4))),
            Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4))));
    }

    private static (int Width, int Height)? ReadJpeg(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8
            || bytes[^2] != 0xff || bytes[^1] != 0xd9) return null;
        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset++] != 0xff) return null;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) return null;
            var marker = bytes[offset++];
            if (marker is 0xd8 or 0xd9) continue;
            if (offset + 2 > bytes.Length) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
            if (length < 2 || offset + length > bytes.Length) return null;
            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                if (length < 7) return null;
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3, 2)));
            }
            offset += length;
        }
        return null;
    }

    private static (int Width, int Height)? ReadWebP(byte[] bytes)
    {
        if (bytes.Length < 30 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) + 8L > bytes.Length) return null;
        var chunk = Encoding.ASCII.GetString(bytes, 12, 4);
        if (chunk == "VP8X")
            return (Read24(bytes, 24) + 1, Read24(bytes, 27) + 1);
        if (chunk == "VP8L" && bytes.Length >= 25 && bytes[20] == 0x2f)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(21, 4));
            return ((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
        }
        if (chunk == "VP8 " && bytes.Length >= 30 && bytes[23] == 0x9d && bytes[24] == 0x01 && bytes[25] == 0x2a)
            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2)) & 0x3fff,
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2)) & 0x3fff);
        return null;
    }

    private static int Read24(byte[] bytes, int offset) =>
        bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);

    private static string? ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0) return "Image has invalid pixel dimensions.";
        if (width > ArtifactCaps.MaxImageDimension || height > ArtifactCaps.MaxImageDimension
            || (long)width * height > ArtifactCaps.MaxImagePixels)
            return $"Image exceeds the {ArtifactCaps.MaxImageDimension}px dimension/pixel cap.";
        return null;
    }

    private static int ParseSvgDimension(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var numeric = new string(raw.Trim().TakeWhile(c => char.IsDigit(c) || c is '.' or '+' or '-').ToArray());
        return double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (int)Math.Ceiling(value)
            : 0;
    }

    private static bool TryReadViewBox(string? raw, out int width, out int height)
    {
        width = height = 0;
        var values = raw?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (values is not { Length: 4 }
            || !double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return false;
        width = (int)Math.Ceiling(w);
        height = (int)Math.Ceiling(h);
        return true;
    }

    private static string? ContentType(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => null,
    };

    private static ArtifactImageValidation Invalid(string error) => new(false, null, null, 0, 0, error);
}
