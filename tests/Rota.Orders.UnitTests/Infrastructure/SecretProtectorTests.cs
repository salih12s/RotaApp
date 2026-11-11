using System.Security.Cryptography;
using Rota.Orders.Api.Infrastructure;

namespace Rota.Orders.UnitTests.Infrastructure;

public sealed class SecretProtectorTests
{
    private readonly SecretProtector _protector = new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Unprotect_WithSameAssociatedData_ReturnsOriginal()
    {
        var protectedText = _protector.Protect("4821", "order-1");

        protectedText.ShouldNotContain("4821");
        _protector.Unprotect(protectedText, "order-1").ShouldBe("4821");
    }

    [Fact]
    public void Protect_SameInputTwice_GivesDifferentCiphertexts()
    {
        // A fresh nonce each time: equal codes must not produce equal ciphertexts in the database.
        _protector.Protect("4821", "order-1").ShouldNotBe(_protector.Protect("4821", "order-1"));
    }

    [Fact]
    public void Unprotect_WithOtherAssociatedData_Throws()
    {
        // A ciphertext copied from another order's row must not decrypt there.
        var protectedText = _protector.Protect("4821", "order-1");

        Should.Throw<AuthenticationTagMismatchException>(() => _protector.Unprotect(protectedText, "order-2"));
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_Throws()
    {
        var bytes = Convert.FromBase64String(_protector.Protect("4821", "order-1"));
        bytes[^1] ^= 0x01;

        Should.Throw<AuthenticationTagMismatchException>(() => _protector.Unprotect(Convert.ToBase64String(bytes), "order-1"));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void Constructor_WithKeyNot32Bytes_Throws(int length) =>
        Should.Throw<ArgumentException>(() => new SecretProtector(new byte[length]));
}
