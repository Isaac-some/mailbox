using MailArchiver.Models;
using MailArchiver.Services.Core;
using MailArchiver.Services.MailProviders;
using MailArchiver.Services.Providers.Imap;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailArchiver.Services;

public sealed class OutboundMailService : IOutboundMailService
{
    private readonly IMailProviderRegistry _mailProviderRegistry;
    private readonly ImapConnectionFactory _imapConnectionFactory;
    private readonly IImapFolderService _folderService;
    private readonly EmailCoreService _emailCoreService;
    private readonly OutboundMailOptions _options;
    private readonly ILogger<OutboundMailService> _logger;

    public OutboundMailService(
        IMailProviderRegistry mailProviderRegistry,
        ImapConnectionFactory imapConnectionFactory,
        IImapFolderService folderService,
        EmailCoreService emailCoreService,
        IOptions<OutboundMailOptions> options,
        ILogger<OutboundMailService> logger)
    {
        _mailProviderRegistry = mailProviderRegistry;
        _imapConnectionFactory = imapConnectionFactory;
        _folderService = folderService;
        _emailCoreService = emailCoreService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<OutboundMailResult> SendAsync(
        MailAccount account,
        OutboundMailMessage request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("当前运行环境未启用发件功能。");
        if (!CanSend(account))
            throw new InvalidOperationException("该账号没有可用的发件凭据。");

        var message = BuildMimeMessage(account, request);
        var module = _mailProviderRegistry.For(account);
        var providerResult = await module.SendAsync(account, message, cancellationToken);
        var sentCopySavedByProvider = providerResult.SentCopySavedByProvider;

        var sentCopySaved = sentCopySavedByProvider;
        if (!sentCopySavedByProvider)
        {
            try
            {
                await SaveToSentFolderAsync(account, message, cancellationToken);
                sentCopySaved = true;
            }
            catch (Exception ex)
            {
                sentCopySaved = false;
                _logger.LogWarning(ex, "Mail {MessageId} was sent but could not be appended to Sent for account {AccountId}",
                    message.MessageId, account.Id);
            }
        }

        try
        {
            await _emailCoreService.ArchiveEmailAsync(account, message, isOutgoing: true, folderName: "Sent");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mail {MessageId} was sent but could not be archived locally for account {AccountId}",
                message.MessageId, account.Id);
        }

        return new OutboundMailResult(sentCopySaved, message.MessageId!);
    }

    public static MimeMessage BuildMimeMessage(MailAccount account, OutboundMailMessage request)
    {
        var message = new MimeMessage
        {
            Subject = request.Subject.Trim(),
            Date = DateTimeOffset.Now,
            MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId()
        };
        message.From.Add(new MailboxAddress(account.Name, account.EmailAddress));
        message.To.AddRange(request.To.Select(MailboxAddress.Parse));
        message.Cc.AddRange(request.Cc.Select(MailboxAddress.Parse));

        var body = new BodyBuilder { TextBody = request.Body };
        foreach (var attachment in request.Attachments)
        {
            body.Attachments.Add(
                Path.GetFileName(attachment.FileName),
                attachment.Content,
                ContentType.Parse(string.IsNullOrWhiteSpace(attachment.ContentType)
                    ? "application/octet-stream"
                    : attachment.ContentType));
        }
        message.Body = body.ToMessageBody();
        return message;
    }

    public bool CanSend(MailAccount account)
    {
        try
        {
            return _mailProviderRegistry.For(account).Inspect(account).CanSend;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task SaveToSentFolderAsync(
        MailAccount account,
        MimeMessage message,
        CancellationToken cancellationToken)
    {
        using var client = _imapConnectionFactory.CreateImapClient(account.Name);
        await _imapConnectionFactory.ConnectAccountAsync(client, account);

        IMailFolder? sentFolder = null;
        try
        {
            sentFolder = client.GetFolder(SpecialFolder.Sent);
        }
        catch (Exception ex) when (ex is FolderNotFoundException or NotSupportedException)
        {
            // Some servers do not advertise SPECIAL-USE; use the existing localized fallback.
        }

        if (sentFolder is null)
        {
            var folders = await _folderService.GetAllFoldersAsync(client, account.Name);
            sentFolder = folders.FirstOrDefault(_folderService.IsOutgoingFolder);
        }

        if (sentFolder is null)
            throw new InvalidOperationException("邮箱服务商没有返回可写入的“已发送”文件夹。");

        await sentFolder.AppendAsync(message, MessageFlags.Seen, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
