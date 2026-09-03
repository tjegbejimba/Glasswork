using System;
using System.IO;
using System.Text;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public sealed record ArtifactTextReadResult(string? Content, string? Error, bool IsOverCap)
{
    public bool Success => Content is not null;
}

/// <summary>Bounded, strict UTF-8 reader for untrusted text-like Artifacts.</summary>
public static class ArtifactTextReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static ArtifactTextReadResult Read(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > maxBytes)
            {
                return new(null, $"File exceeds the {ArtifactRow.FormatSize(maxBytes)} inline text limit.", true);
            }

            var bytes = File.ReadAllBytes(path);
            var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
                ? Encoding.UTF8.Preamble.Length
                : 0;
            return new(StrictUtf8.GetString(bytes, offset, bytes.Length - offset), null, false);
        }
        catch (DecoderFallbackException)
        {
            return new(null, "Content is not valid UTF-8 text.", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, ex.Message, false);
        }
    }
}
