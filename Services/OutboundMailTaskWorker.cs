using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services;

public sealed class OutboundMailTaskWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboundMailOptions _options;
    private readonly ILogger<OutboundMailTaskWorker> _logger;

    public OutboundMailTaskWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboundMailOptions> options,
        ILogger<OutboundMailTaskWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        await MarkInterruptedItemsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessNextDueItemAsync(stoppingToken);
                if (!processed)
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbound task worker loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    internal async Task<bool> ProcessNextDueItemAsync(CancellationToken cancellationToken)
    {
        int itemId;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
            var item = await context.OutboundMailTaskItems
                .Where(candidate =>
                    candidate.Status == OutboundMailTaskItemStatus.Pending &&
                    candidate.ScheduledAtUtc <= DateTime.UtcNow)
                .OrderBy(candidate => candidate.ScheduledAtUtc)
                .ThenBy(candidate => candidate.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (item is null)
                return false;

            item.Status = OutboundMailTaskItemStatus.Sending;
            item.StartedAtUtc = DateTime.UtcNow;
            item.ErrorMessage = null;
            await context.SaveChangesAsync(cancellationToken);
            itemId = item.Id;
        }

        await using var sendScope = _scopeFactory.CreateAsyncScope();
        var sendContext = sendScope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
        var sendService = sendScope.ServiceProvider.GetRequiredService<IOutboundMailService>();
        var claimedItem = await sendContext.OutboundMailTaskItems
            .Include(item => item.MailAccount)
            .FirstAsync(item => item.Id == itemId, cancellationToken);

        try
        {
            var result = await sendService.SendAsync(
                claimedItem.MailAccount,
                new OutboundMailMessage(
                    [claimedItem.Recipient],
                    [],
                    claimedItem.Subject,
                    claimedItem.Body,
                    []),
                cancellationToken);

            claimedItem.Status = result.SentCopySaved
                ? OutboundMailTaskItemStatus.Sent
                : OutboundMailTaskItemStatus.SentWithoutCopy;
            claimedItem.MessageId = result.MessageId;
            claimedItem.SentCopySaved = result.SentCopySaved;
            claimedItem.CompletedAtUtc = DateTime.UtcNow;
            claimedItem.ErrorMessage = result.SentCopySaved
                ? null
                : "邮件已发出，但“已发送”副本或本地归档未完整保存，请勿重复发送。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            claimedItem.Status = OutboundMailTaskItemStatus.Interrupted;
            claimedItem.ErrorMessage = "发送过程被应用关闭打断，结果不确定；为避免重复邮件，未自动重试。";
            claimedItem.CompletedAtUtc = DateTime.UtcNow;
            await sendContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            claimedItem.Status = OutboundMailTaskItemStatus.Failed;
            claimedItem.ErrorMessage = ToSafeError(ex);
            claimedItem.CompletedAtUtc = DateTime.UtcNow;
            _logger.LogWarning(ex, "Outbound task item {ItemId} failed for account {AccountId}", claimedItem.Id, claimedItem.MailAccountId);
        }

        await sendContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task MarkInterruptedItemsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
        var items = await context.OutboundMailTaskItems
            .Where(item => item.Status == OutboundMailTaskItemStatus.Sending)
            .ToListAsync(cancellationToken);
        if (items.Count == 0)
            return;

        foreach (var item in items)
        {
            item.Status = OutboundMailTaskItemStatus.Interrupted;
            item.CompletedAtUtc = DateTime.UtcNow;
            item.ErrorMessage = "上次发送时应用意外退出，结果不确定；为避免重复邮件，未自动重试。";
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    private static string ToSafeError(Exception exception)
    {
        var message = exception switch
        {
            MailKit.Security.AuthenticationException => "发件认证失败，请检查 Refresh Token 是否包含 SMTP.Send 权限。",
            TimeoutException => "连接发件服务器超时。",
            _ => "发送失败，邮件尚未确认发出。请检查账号授权和网络后重试。"
        };
        return message;
    }
}
