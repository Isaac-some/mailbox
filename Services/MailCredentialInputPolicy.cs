using System.Globalization;

namespace MailArchiver.Services;

/// <summary>Normalizes generated authorization values copied by hand, before storage or comparison.</summary>
public static class MailCredentialInputPolicy
{
    public const int MaxCredentialLength = 16_384;

    public static string Normalize(string? value)
        => string.Concat((value ?? string.Empty).Where(character =>
            !char.IsWhiteSpace(character)
            && char.GetUnicodeCategory(character) != UnicodeCategory.Format));

    public static string NormalizeAndValidate(string? value)
    {
        var credential = Normalize(value);
        if (credential.Length == 0)
            throw new InvalidOperationException("授权码不能为空；清理空格、换行和不可见字符后没有有效内容。");
        if (credential.Length > MaxCredentialLength)
            throw new InvalidOperationException($"授权码过长，最多支持 {MaxCredentialLength} 个字符；请检查是否误粘贴了整段内容。");
        if (credential.Any(char.IsControl) || credential.Any(char.IsSurrogate))
            throw new InvalidOperationException("授权码含异常控制字符或不支持的字符，请重新复制完整授权码。");
        return credential;
    }
}
