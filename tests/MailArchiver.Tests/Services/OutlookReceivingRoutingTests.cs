using MailArchiver.Models;
using MailArchiver.Services.Providers.Outlook;
using MailArchiver.Services.Factories;
using MailArchiver.Services.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
namespace MailArchiver.Tests.Services;
public sealed class OutlookReceivingRoutingTests
{
    [Fact]
    public void Personal_Outlook_receiving_must_not_be_forced_through_IMAP()
    {
        var imap = new ImapEmailService(null!, null!, null!, null!, null!, NullLogger<ImapEmailService>.Instance);
        using var services = new ServiceCollection().AddSingleton(imap).AddSingleton(new OutlookEmailService(imap, null!, null!, null!, null!, null!)).BuildServiceProvider();
        var factory = new ProviderEmailServiceFactory(services, null!, NullLogger<ProviderEmailServiceFactory>.Instance);
        Assert.IsType<OutlookEmailService>(factory.GetService(ProviderType.MSA));
        Assert.Same(imap, factory.GetService(ProviderType.IMAP));
    }
}
