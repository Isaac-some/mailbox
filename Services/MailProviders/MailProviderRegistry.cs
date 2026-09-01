using MailArchiver.Models;

namespace MailArchiver.Services.MailProviders;

public sealed class MailProviderRegistry : IMailProviderRegistry
{
    private readonly IReadOnlyDictionary<MailProviderKind, IMailProviderModule> _modules;

    public MailProviderRegistry(IEnumerable<IMailProviderModule> modules)
    {
        var materialized = modules.ToList();
        _modules = materialized.ToDictionary(module => module.Kind);

        var missing = Enum.GetValues<MailProviderKind>()
            .Where(kind => kind != MailProviderKind.Custom)
            .Where(kind => !_modules.ContainsKey(kind))
            .ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"缺少邮箱模块：{string.Join("、", missing)}。");
    }

    public IMailProviderModule For(MailProviderKind kind)
        => _modules.TryGetValue(kind, out var module)
            ? module
            : throw new NotSupportedException($"邮箱服务商“{kind}”没有可用模块。");

    public IMailProviderModule For(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.MailProviderKind is null)
            throw new InvalidOperationException($"账号“{account.EmailAddress}”缺少明确的邮箱服务商身份。");

        return For(account.MailProviderKind.Value);
    }

    public IMailProviderModule Detect(string emailAddress)
    {
        var matches = _modules.Values
            .Where(module => module.SupportsAddress(emailAddress))
            .ToList();

        if (matches.Count == 1)
            return matches[0];
        if (matches.Count == 0 && _modules.TryGetValue(MailProviderKind.Custom, out var custom))
            return custom;
        if (matches.Count == 0)
            throw new NotSupportedException("目前没有可用的自定义域名邮箱模块。");
        throw new InvalidOperationException($"邮箱“{emailAddress}”匹配到多个邮箱模块。");
    }
}
