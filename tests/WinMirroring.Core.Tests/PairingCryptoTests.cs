using WinMirroring.Core.Discovery;
using WinMirroring.Core.Pairing;

namespace WinMirroring.Core.Tests;

public class PairingCryptoTests
{
    private const string Laptop = "4F1A9C0D33B2E7A1C5D6F7081920AB3C4D5E6F708192A3B4C5D6E7F80910A1B2";
    private const string Tablet = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";

    [Fact]
    public void BothDevicesShowTheSameCode()
    {
        Assert.Equal(PairingCrypto.ConfirmationCode(Laptop, Tablet), PairingCrypto.ConfirmationCode(Tablet, Laptop));
        Assert.Equal(PairingCrypto.ConfirmationCode(Laptop, Tablet), PairingCrypto.ConfirmationCode(Laptop.ToLowerInvariant(), Tablet));
    }

    [Fact]
    public void CodeIsSixDigitsInTwoGroups()
    {
        var code = PairingCrypto.ConfirmationCode(Laptop, Tablet);

        Assert.Matches("^[0-9]{3} [0-9]{3}$", code);
    }

    [Fact]
    public void AnotherCertificateGivesAnotherCode()
    {
        var impostor = "0" + Tablet[1..];

        Assert.NotEqual(PairingCrypto.ConfirmationCode(Laptop, Tablet), PairingCrypto.ConfirmationCode(Laptop, impostor));
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
