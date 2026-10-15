using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace MailBot.Core.Security;

/// <summary>
/// 凭据保护：使用 Windows DPAPI 加密/解密账号密码。
/// 设计文档 3 节：DPAPI (ProtectedData)，密码不落明文，换机器需要重新配置。
/// 仅在 Windows 上可用（net8.0-windows 已默认包含）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class CredentialProtector
{
    /// <summary>用当前 Windows 用户作用域加密明文。</summary>
    public static byte[] Protect(string plainText)
    {
        if (plainText is null) throw new ArgumentNullException(nameof(plainText));
        var bytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        return ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    /// <summary>用当前 Windows 用户作用域解密密文。</summary>
    public static string Unprotect(byte[] cipher)
    {
        if (cipher is null) throw new ArgumentNullException(nameof(cipher));
        var bytes = ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}