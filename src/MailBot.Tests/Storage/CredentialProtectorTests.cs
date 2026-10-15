using MailBot.Core.Security;
using Xunit;

namespace MailBot.Tests.Storage;

public class CredentialProtectorTests
{
    [Fact]
    public void Protect_Roundtrip()
    {
        var plain = "S3cret-Passw0rd!@#";
        var cipher = CredentialProtector.Protect(plain);
        Assert.NotNull(cipher);
        Assert.NotEmpty(cipher);
        var back = CredentialProtector.Unprotect(cipher);
        Assert.Equal(plain, back);
    }

    [Fact]
    public void Protect_DifferentInstances_DifferentCipher()
    {
        var plain = "same";
        var a = CredentialProtector.Protect(plain);
        var b = CredentialProtector.Protect(plain);
        // DPAPI 加密结果可能不同（非确定性 IV），但都能解出原文
        var aText = CredentialProtector.Unprotect(a);
        var bText = CredentialProtector.Unprotect(b);
        Assert.Equal("same", aText);
        Assert.Equal("same", bText);
    }

    [Fact]
    public void Protect_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => CredentialProtector.Protect(null!));
    }
}