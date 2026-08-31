using MailArchiver.Controllers;

namespace MailArchiver.Tests.Controllers;

public class EmailHtmlSanitizerSecurityTests
{
    [Fact]
    public void SanitizeHtmlFragmentForDisplay_RemovesAnnotationXmlMutationXssPayload()
    {
        const string payload =
            "<math><annotation-xml encoding=\"text/html\">" +
            "<title><a encoding=\"</title><img src=x onerror=alert()>\">" +
            "</annotation-xml></math>";

        var sanitized = EmailsController.SanitizeHtmlFragmentForDisplay(payload);

        Assert.DoesNotContain("onerror", sanitized, StringComparison.OrdinalIgnoreCase);
    }
}
