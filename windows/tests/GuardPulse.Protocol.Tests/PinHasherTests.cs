namespace GuardPulse.Protocol.Tests;

using System;
using GuardPulse.Protocol;
using Xunit;

public class PinHasherTests
{
    // Precomputed cross-language vectors (PBKDF2-HMAC-SHA256, RFC 2898; matches Java's
    // PBKDF2WithHmacSHA256 and hashlib.pbkdf2_hmac). Salt "c2FsdC1mb3ItdGVzdA" = "salt-for-test"
    // (13 bytes, the same fixture salt as PinHasherTest.legacyHashesRemainCompatible).
    private const string LegacySalt = "c2FsdC1mb3ItdGVzdA";
    private const string LegacyHash = "c96IovgSojY2idvpsYt4hvR1BjMwNlvaVDp5SbOf258"; // sha256("<salt>:123456")
    private const string V2Salt = "MDEyMzQ1Njc4OWFiY2RlZg"; // "0123456789abcdef", 16 bytes -> 22 chars
    private const string V2Hash210k = "9UI-qC10YGAs3EmhUfE4S5cFc2P_Psp3Kwc3_nbS1gc";
    private const string V2Salt18Hash210k = "o1YwxNW_6uzHwvWYHi5wK9QjaKqlhHu-Ebv81Mq7Ktg";
    private const string V2Salt18Hash1m = "SJIDBZx6aSpXRsl3hU5aSdHXaUlqbR9CANMo8WsGJoo";

    [Fact]
    public void CreateProducesBase64UrlShapes()
    {
        var created = PinHasher.Create("123456");

        // 16-byte salt -> 22 base64url chars; 32-byte derived key -> 43 base64url chars.
        Assert.Matches("^[A-Za-z0-9_-]{22}$", created.Salt);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", created.Hash);
    }

    [Fact]
    public void CreateUsesFreshRandomSalts()
    {
        var first = PinHasher.Create("123456");
        var second = PinHasher.Create("123456");

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("12 456")]
    [InlineData("")]
    [InlineData("0000000")]
    public void CreateRejectsNonSixDigitPins(string pin)
    {
        Assert.Throws<ArgumentException>(() => PinHasher.Create(pin));
    }

    [Fact]
    public void CreateRejectsNullPin()
    {
        Assert.Throws<ArgumentException>(() => PinHasher.Create(null!));
    }

    [Fact]
    public void VerifiesOnlyMatchingPin()
    {
        // Ported from PinHasherTest.verifiesOnlyMatchingPin.
        var created = PinHasher.Create("123456");

        Assert.True(PinHasher.Verify("123456", created.Salt, created.Hash, PinHasher.CURRENT_VERSION, PinHasher.ALGORITHM, PinHasher.ITERATIONS));
        Assert.False(PinHasher.Verify("654321", created.Salt, created.Hash, PinHasher.CURRENT_VERSION, PinHasher.ALGORITHM, PinHasher.ITERATIONS));
        Assert.False(PinHasher.Verify("", created.Salt, created.Hash, PinHasher.CURRENT_VERSION, PinHasher.ALGORITHM, PinHasher.ITERATIONS));
    }

    [Fact]
    public void VerifiesDeterministicV2Vector()
    {
        Assert.True(PinHasher.Verify("123456", V2Salt, V2Hash210k, PinHasher.CURRENT_VERSION, PinHasher.ALGORITHM, 210_000));
        Assert.False(PinHasher.Verify("654321", V2Salt, V2Hash210k, PinHasher.CURRENT_VERSION, PinHasher.ALGORITHM, 210_000));
        Assert.False(PinHasher.Verify("123456", V2Salt, V2Hash210k, PinHasher.CURRENT_VERSION, "SHA-256", 210_000));
    }

    [Fact]
    public void VerifiesIterationBounds()
    {
        // algorithm null falls back to the current algorithm; iterations null falls back to ITERATIONS.
        var created = PinHasher.Create("123456");
        Assert.True(PinHasher.Verify("123456", created.Salt, created.Hash, PinHasher.CURRENT_VERSION, null, null));

        Assert.True(PinHasher.Verify("123456", LegacySalt, V2Salt18Hash210k, PinHasher.CURRENT_VERSION, null, 210_000));
        Assert.True(PinHasher.Verify("123456", LegacySalt, V2Salt18Hash1m, PinHasher.CURRENT_VERSION, null, 1_000_000));

        // Out-of-range iteration counts are rejected outright (no derivation attempted).
        Assert.False(PinHasher.Verify("123456", LegacySalt, V2Salt18Hash210k, PinHasher.CURRENT_VERSION, null, 209_999));
        Assert.False(PinHasher.Verify("123456", LegacySalt, V2Salt18Hash1m, PinHasher.CURRENT_VERSION, null, 1_000_001));

        // Any other algorithm name is rejected for v2.
        Assert.False(PinHasher.Verify("123456", LegacySalt, V2Salt18Hash210k, PinHasher.CURRENT_VERSION, "PBKDF2WithHmacSHA1", 210_000));
    }

    [Fact]
    public void LegacyHashesRemainCompatible()
    {
        // Ported from PinHasherTest.legacyHashesRemainCompatible.
        Assert.True(PinHasher.Verify("123456", LegacySalt, LegacyHash, PinHasher.LEGACY_VERSION));
        Assert.False(PinHasher.Verify("654321", LegacySalt, LegacyHash, PinHasher.LEGACY_VERSION));

        // The contract's default version is the legacy v1 scheme.
        Assert.True(PinHasher.Verify("123456", LegacySalt, LegacyHash));
    }

    [Fact]
    public void BlankSaltOrHashFailsClosed()
    {
        Assert.False(PinHasher.Verify("123456", "", LegacyHash, PinHasher.LEGACY_VERSION));
        Assert.False(PinHasher.Verify("123456", "   ", LegacyHash, PinHasher.LEGACY_VERSION));
        Assert.False(PinHasher.Verify("123456", LegacySalt, "", PinHasher.LEGACY_VERSION));
        Assert.False(PinHasher.Verify("123456", LegacySalt, "   ", PinHasher.LEGACY_VERSION));
        Assert.False(PinHasher.Verify("123456", null!, LegacyHash, PinHasher.LEGACY_VERSION));
        Assert.False(PinHasher.Verify("123456", LegacySalt, null!, PinHasher.LEGACY_VERSION));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void UnknownVersionsAreRejected(int version)
    {
        Assert.False(PinHasher.Verify("123456", V2Salt, V2Hash210k, version, PinHasher.ALGORITHM, 210_000));
    }
}
