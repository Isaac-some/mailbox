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

    [Fact]
    public void SanitizeHtmlFragmentForDisplay_Preserves_class_based_email_buttons()
    {
        const string html = "<style>.cta{display:inline-block;background:#07f;color:#fff;padding:12px}</style>" +
            "<a class=\"cta\" href=\"https://example.com/action\" onclick=\"alert(1)\">打开</a>";

        var sanitized = EmailsController.SanitizeHtmlFragmentForDisplay(html);

        Assert.Contains("class=\"cta\"", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cta", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", sanitized, StringComparison.OrdinalIgnoreCase);
    }
}
