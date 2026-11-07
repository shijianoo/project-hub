using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Infrastructure.Configuration;

namespace ProjectHub.Infrastructure.Security;

/// <summary>生成、保存并一次性校验图形验证码。</summary>
public sealed class CaptchaService : ICaptchaService
{
    private const string CodeAlphabet = "23456789";
    private const string CacheKeyPrefix = "projecthub:captcha:";
    private readonly ConcurrentDictionary<string, CaptchaEntry> challenges = new(StringComparer.Ordinal);
    private readonly CaptchaOptions options;
    private readonly Func<int, string> codeFactory;

    public CaptchaService(IOptions<CaptchaOptions> options)
        : this(options, GenerateCode)
    {
    }

    internal CaptchaService(IOptions<CaptchaOptions> options, Func<int, string> codeFactory)
    {
        this.options = options.Value;
        this.codeFactory = codeFactory;
        ValidateOptions(this.options);
    }

    /// <inheritdoc />
    public CaptchaChallengeResponse Create()
    {
        DateTime now = DateTime.UtcNow;
        RemoveExpiredChallenges(now);
        TrimToCapacity();

        string captchaId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string code = codeFactory(options.CodeLength);
        DateTime expiresAt = now.AddSeconds(options.ExpirationSeconds);
        byte[] answerHash = ComputeAnswerHash(captchaId, code);

        challenges[BuildCacheKey(captchaId)] = new CaptchaEntry(answerHash, expiresAt);
        return new CaptchaChallengeResponse
        {
            CaptchaId = captchaId,
            ImageDataUrl = CaptchaImageRenderer.RenderDataUrl(code),
            ExpiresAt = expiresAt
        };
    }

    /// <inheritdoc />
    public void Validate(string captchaId, string captchaCode)
    {
        if (string.IsNullOrWhiteSpace(captchaId) || string.IsNullOrWhiteSpace(captchaCode))
        {
            throw new BusinessException("请输入图形验证码", "CAPTCHA_REQUIRED");
        }

        string normalizedId = captchaId.Trim().ToLowerInvariant();
        if (!challenges.TryRemove(BuildCacheKey(normalizedId), out CaptchaEntry? entry) || entry.ExpiresAt <= DateTime.UtcNow)
        {
            throw new BusinessException("验证码已失效，请点击图片刷新后重试", "CAPTCHA_EXPIRED");
        }

        byte[] submittedHash = ComputeAnswerHash(normalizedId, captchaCode);
        if (!CryptographicOperations.FixedTimeEquals(entry.AnswerHash, submittedHash))
        {
            throw new BusinessException("图形验证码错误，请重新输入", "CAPTCHA_INVALID");
        }
    }

    private static string BuildCacheKey(string captchaId) => $"{CacheKeyPrefix}{captchaId}";

    private static byte[] ComputeAnswerHash(string captchaId, string code)
    {
        string normalizedCode = code.Trim().ToUpperInvariant();
        return SHA256.HashData(Encoding.UTF8.GetBytes($"{captchaId}:{normalizedCode}"));
    }

    private static string GenerateCode(int length)
    {
        char[] characters = new char[length];
        for (int index = 0; index < characters.Length; index++)
        {
            characters[index] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return new string(characters);
    }

    private static void ValidateOptions(CaptchaOptions value)
    {
        if (value.CodeLength is < 4 or > 6)
        {
            throw new InvalidOperationException("Captcha:CodeLength 必须介于 4 到 6 之间");
        }

        if (value.ExpirationSeconds is < 30 or > 600)
        {
            throw new InvalidOperationException("Captcha:ExpirationSeconds 必须介于 30 到 600 之间");
        }

        if (value.MaximumActiveChallenges is < 100 or > 100000)
        {
            throw new InvalidOperationException("Captcha:MaximumActiveChallenges 必须介于 100 到 100000 之间");
        }
    }

    private void RemoveExpiredChallenges(DateTime now)
    {
        foreach ((string key, CaptchaEntry value) in challenges)
        {
            if (value.ExpiresAt <= now)
            {
                challenges.TryRemove(key, out _);
            }
        }
    }

    private void TrimToCapacity()
    {
        int overflow = challenges.Count - options.MaximumActiveChallenges + 1;
        if (overflow <= 0)
        {
            return;
        }

        IEnumerable<string> oldestKeys = challenges
            .OrderBy(item => item.Value.ExpiresAt)
            .Take(overflow)
            .Select(item => item.Key);
        foreach (string key in oldestKeys)
        {
            challenges.TryRemove(key, out _);
        }
    }

    private sealed record CaptchaEntry(byte[] AnswerHash, DateTime ExpiresAt);
}
