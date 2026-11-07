using WinMirroring.Core.Discovery;
using WinMirroring.Core.Pairing;

namespace WinMirroring.Core.Tests;

public class PairingCryptoTests
{
    [Fact]
    public void BothSidesComputeTheSameProofFromTheSamePin()
    {
        var nonce = PairingCrypto.GenerateNonce();
        var initiatorKey = PairingCrypto.DeriveKey("123 456");
        var responderKey = PairingCrypto.DeriveKey("123456");

        var fromInitiator = PairingCrypto.ComputeProof(initiatorKey, PairingRole.Initiator, "aa", "BB", nonce);
        var expectedByResponder = PairingCrypto.ComputeProof(responderKey, PairingRole.Initiator, "AA", "bb", nonce);

        Assert.True(PairingCrypto.ProofMatches(expectedByResponder, Convert.ToBase64String(fromInitiator)));
    }

    [Fact]
    public void ProofDependsOnPinRoleFingerprintsAndNonce()
    {
        var nonce = PairingCrypto.GenerateNonce();
        var key = PairingCrypto.DeriveKey("123456");
        var baseline = PairingCrypto.ComputeProof(key, PairingRole.Initiator, "AA", "BB", nonce);

        Assert.NotEqual(baseline, PairingCrypto.ComputeProof(PairingCrypto.DeriveKey("654321"), PairingRole.Initiator, "AA", "BB", nonce));
        Assert.NotEqual(baseline, PairingCrypto.ComputeProof(key, PairingRole.Responder, "AA", "BB", nonce));
        Assert.NotEqual(baseline, PairingCrypto.ComputeProof(key, PairingRole.Initiator, "BB", "AA", nonce));
        Assert.NotEqual(baseline, PairingCrypto.ComputeProof(key, PairingRole.Initiator, "AA", "BB", PairingCrypto.GenerateNonce()));
    }

    [Fact]
    public void ProofMatchesRejectsBrokenInput()
    {
        var expected = PairingCrypto.ComputeProof(PairingCrypto.DeriveKey("1"), PairingRole.Initiator, "A", "B", new byte[32]);

        Assert.False(PairingCrypto.ProofMatches(expected, null));
        Assert.False(PairingCrypto.ProofMatches(expected, ""));
        Assert.False(PairingCrypto.ProofMatches(expected, "not base64!"));
        Assert.False(PairingCrypto.ProofMatches(expected, Convert.ToBase64String(new byte[32])));
    }

    [Fact]
    public void GeneratedPinHasSixDigits()
    {
        for (var i = 0; i < 100; i++)
        {
            var pin = PairingCrypto.GeneratePin();
            Assert.Equal(6, pin.Length);
            Assert.All(pin, c => Assert.True(char.IsAsciiDigit(c)));
        }
    }

    [Theory]
    [InlineData("DESKTOP-P50HIEF", 47620, "DESKTOP-P50HIEF", 47620)]
    [InlineData(" 192.168.1.191:5000 ", 47620, "192.168.1.191", 5000)]
    [InlineData("host:notaport", 47620, "host:notaport", 47620)]
    public void ManualAddressParses(string text, int defaultPort, string host, int port)
    {
        Assert.True(ManualAddress.TryParse(text, defaultPort, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    [InlineData(null)]
    public void ManualAddressRejectsInvalid(string? text)
    {
        Assert.False(ManualAddress.TryParse(text, 47620, out _, out _));
    }
}
