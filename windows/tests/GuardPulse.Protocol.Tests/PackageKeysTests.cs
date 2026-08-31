namespace GuardPulse.Protocol.Tests;

using System.Collections.Generic;
using GuardPulse.Protocol;
using Xunit;

public class PackageKeysTests
{
    public static IEnumerable<object[]> RoundTripInputs()
    {
        yield return new object[] { "com.google.android.youtube.tv" };
        yield return new object[] { "com.youtube.tv" };
        yield return new object[] { "com.netflix.app" };
        yield return new object[] { "guardpulse.windows.taskmgr" };
        yield return new object[] { "com.guardpulse.parentcontrol.parent" };
        yield return new object[] { "a" };
        yield return new object[] { "" };
        yield return new object[] { "package with spaces and + / special=chars" };
        yield return new object[] { "trailing.dot." };
        yield return new object[] { "unicode-\u4f60\u597d-\u00e9" };
        yield return new object[] { "C:\\Path\\To\\App.exe" };
    }

    [Theory]
    [InlineData("com.google.android.youtube.tv", "Y29tLmdvb2dsZS5hbmRyb2lkLnlvdXR1YmUudHY")]
    [InlineData("com.youtube.tv", "Y29tLnlvdXR1YmUudHY")]
    [InlineData("com.netflix.app", "Y29tLm5ldGZsaXguYXBw")]
    [InlineData("guardpulse.windows.taskmgr", "Z3VhcmRwdWxzZS53aW5kb3dzLnRhc2ttZ3I")]
    [InlineData("a", "YQ")]
    [InlineData("abc", "YWJj")]
    public void EncodeProducesUnpaddedBase64Url(string packageName, string expected)
    {
        var encoded = PackageKeys.Encode(packageName);

        Assert.Equal(expected, encoded);
        Assert.DoesNotContain(".", encoded);
        Assert.Matches("^[A-Za-z0-9_-]*$", encoded);
    }

    [Theory]
    [MemberData(nameof(RoundTripInputs))]
    public void RoundTripsPackageName(string packageName)
    {
        var encoded = PackageKeys.Encode(packageName);

        // Ported from PackageKeysTest.roundTripPackageName: keys must be Firebase-safe
        // (no dots) and decode back to the exact package name.
        Assert.DoesNotContain(".", encoded);
        Assert.Equal(packageName, PackageKeys.Decode(encoded));
    }

    [Fact]
    public void DecodeAcceptsPaddedBase64Url()
    {
        // "YWJj" padded with '=' decodes identically (Firebase keys are unpadded, but the
        // decoder accepts padded input like java.util.Base64.getUrlDecoder()).
        Assert.Equal("abc", PackageKeys.Decode("YWJj="));
        Assert.Equal("a", PackageKeys.Decode("YQ=="));
    }
}
