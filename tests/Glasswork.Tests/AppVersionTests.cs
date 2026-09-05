using Microsoft.VisualStudio.TestTools.UnitTesting;
using Glasswork.Core.AppUpdate;

namespace Glasswork.Tests;

[TestClass]
public class AppVersionTests
{
    [TestMethod]
    public void Parse_BasicVersion_Success()
    {
        // Tracer bullet: parse "1.3.0"
        var result = AppVersion.TryParse("1.3.0", out var version);

        Assert.IsTrue(result);
        Assert.IsNotNull(version);
        Assert.AreEqual(1, version.Major);
        Assert.AreEqual(3, version.Minor);
        Assert.AreEqual(0, version.Patch);
    }

    [TestMethod]
    public void Parse_FourComponentVersion_IgnoresFourthComponent()
    {
        var result = AppVersion.TryParse("1.3.0.0", out var version);

        Assert.IsTrue(result);
        Assert.IsNotNull(version);
        Assert.AreEqual(1, version.Major);
        Assert.AreEqual(3, version.Minor);
        Assert.AreEqual(0, version.Patch);
    }

    [TestMethod]
    public void Parse_VersionWithVPrefix_StripsPrefix()
    {
        var result = AppVersion.TryParse("v1.4.0", out var version);

        Assert.IsTrue(result);
        Assert.IsNotNull(version);
        Assert.AreEqual(1, version.Major);
        Assert.AreEqual(4, version.Minor);
        Assert.AreEqual(0, version.Patch);
    }

    [TestMethod]
    public void Parse_GarbageInput_ReturnsFalse()
    {
        var result = AppVersion.TryParse("not-a-version", out var version);

        Assert.IsFalse(result);
        Assert.IsNull(version);
    }

    [TestMethod]
    public void Parse_EmptyString_ReturnsFalse()
    {
        var result = AppVersion.TryParse("", out var version);

        Assert.IsFalse(result);
        Assert.IsNull(version);
    }

    [TestMethod]
    public void Parse_Null_ReturnsFalse()
    {
        var result = AppVersion.TryParse(null!, out var version);

        Assert.IsFalse(result);
        Assert.IsNull(version);
    }

    [TestMethod]
    public void Parse_NegativeVersion_ReturnsFalse()
    {
        var result = AppVersion.TryParse("-1.2.3", out var version);

        Assert.IsFalse(result);
        Assert.IsNull(version);
    }

    [TestMethod]
    public void Parse_TooManyComponents_ReturnsFalse()
    {
        var result = AppVersion.TryParse("1.2.3.4.5", out var version);

        Assert.IsFalse(result);
        Assert.IsNull(version);
    }

    [TestMethod]
    public void Parse_FourthComponentGarbage_ReturnsFalse()
    {
        var result = AppVersion.TryParse("1.2.3.garbage", out var version);

        Assert.IsFalse(result);
        Assert.IsNull(version);
    }

    [TestMethod]
    public void Compare_EqualVersions_ReturnsZero()
    {
        AppVersion.TryParse("1.3.0", out var v1);
        AppVersion.TryParse("1.3.0", out var v2);

        Assert.AreEqual(0, v1!.CompareTo(v2));
    }

    [TestMethod]
    public void Compare_MinorVersionDifference_OrdersCorrectly()
    {
        AppVersion.TryParse("1.10.0", out var v1);
        AppVersion.TryParse("1.9.0", out var v2);

        Assert.IsGreaterThan(0, v1!.CompareTo(v2));
        Assert.IsLessThan(0, v2!.CompareTo(v1));
    }

    [TestMethod]
    public void Compare_MajorVersionDifference_OrdersCorrectly()
    {
        AppVersion.TryParse("2.0.0", out var v1);
        AppVersion.TryParse("1.9.9", out var v2);

        Assert.IsGreaterThan(0, v1!.CompareTo(v2));
    }

    [TestMethod]
    public void Parse_VersionWithBuildMetadata_StripsBuildMetadata()
    {
        // AssemblyInformationalVersion includes +<commit-sha> by default
        var result = AppVersion.TryParse("1.3.0+8f3a1b2", out var version);

        Assert.IsTrue(result);
        Assert.IsNotNull(version);
        Assert.AreEqual(1, version.Major);
        Assert.AreEqual(3, version.Minor);
        Assert.AreEqual(0, version.Patch);
    }

    [TestMethod]
    public void Parse_VersionWithPreReleaseTag_StripsPreReleaseTag()
    {
        var result = AppVersion.TryParse("1.3.0-beta", out var version);

        Assert.IsTrue(result);
        Assert.IsNotNull(version);
        Assert.AreEqual(1, version.Major);
        Assert.AreEqual(3, version.Minor);
        Assert.AreEqual(0, version.Patch);
    }

    [TestMethod]
    public void Parse_VersionWithPreReleaseAndMetadata_StripsBoth()
    {
        var result = AppVersion.TryParse("1.3.0-beta+abc123", out var version);

        Assert.IsTrue(result);
        Assert.IsNotNull(version);
        Assert.AreEqual(1, version.Major);
        Assert.AreEqual(3, version.Minor);
        Assert.AreEqual(0, version.Patch);
    }

    [TestMethod]
    public void ToString_FormatsAsMajorMinorPatch()
    {
        AppVersion.TryParse("1.4.0", out var version);

        Assert.AreEqual("1.4.0", version!.ToString());
    }
}
