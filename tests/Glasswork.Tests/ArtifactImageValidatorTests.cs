using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public sealed class ArtifactImageValidatorTests
{
    private string _folder = null!;

    [TestInitialize]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "glasswork-image-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void CleanUp() => Directory.Delete(_folder, recursive: true);

    [TestMethod]
    public void Validate_PngHeader_EnforcesDecodeDimensionAndPixelCaps()
    {
        var safe = Path.Combine(_folder, "safe.png");
        var huge = Path.Combine(_folder, "huge.png");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var hugePng = (byte[])png.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
            hugePng.AsSpan(16, 4),
            ArtifactCaps.MaxImageDimension + 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            hugePng.AsSpan(29, 4),
            Crc32(hugePng.AsSpan(12, 17)));
        File.WriteAllBytes(safe, png);
        File.WriteAllBytes(huge, hugePng);

        var accepted = ArtifactImageValidator.Validate(safe);
        var rejected = ArtifactImageValidator.Validate(huge);

        Assert.IsTrue(accepted.IsValid);
        Assert.AreEqual("image/png", accepted.ContentType);
        Assert.IsFalse(rejected.IsValid);
        StringAssert.Contains(rejected.Error, "pixel");
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

    [TestMethod]
    public void Validate_Svg_RemovesExecutableAndRemoteContent()
    {
        var path = Path.Combine(_folder, "hostile.svg");
        File.WriteAllText(path, """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="50" onload="alert(1)">
              <script>alert(1)</script>
              <image href="https://evil.example/tracker.png"/>
              <rect width="100" height="50" fill="blue"/>
            </svg>
            """);

        var result = ArtifactImageValidator.Validate(path);
        var sanitized = System.Text.Encoding.UTF8.GetString(result.Content!);

        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(sanitized.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(sanitized.Contains("onload", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(sanitized.Contains("evil.example", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(sanitized, "<rect");
    }

    [TestMethod]
    public void Validate_TruncatedPng_IsNotInlineCapable()
    {
        var path = Path.Combine(_folder, "truncated.png");
        File.WriteAllBytes(path, [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1]);

        var result = ArtifactImageValidator.Validate(path);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Error, "decoded");
    }
}
