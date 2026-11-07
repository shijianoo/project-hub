using System.Security.Cryptography;

namespace ProjectHub.Infrastructure.Security;

/// <summary>使用 PBKDF2-SHA256 安全派生和校验密码。</summary>
public sealed class PasswordHasher
{
    private const int IterationCount = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// <summary>创建包含算法参数、盐和摘要的密码字符串。</summary>
    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, IterationCount, HashAlgorithmName.SHA256, HashSize);
        return $"PBKDF2-SHA256${IterationCount}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>以固定时间比较方式校验密码。</summary>
    public bool Verify(string password, string encodedHash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(encodedHash))
        {
            return false;
        }

        string[] segments = encodedHash.Split('$');
        if (segments.Length != 4 || !int.TryParse(segments[1], out int iterations))
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(segments[2]);
            byte[] expectedHash = Convert.FromBase64String(segments[3]);
            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
