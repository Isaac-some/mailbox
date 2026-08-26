using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class WhitelistDeletionParserTests
{
    [Fact]
    public void Extracts_unique_com_addresses_from_unstructured_text()
    {
        var result = WhitelistDeletionParser.ExtractComAddresses(
            "保留 Alice@Example.com；重复 alice@example.com\nBob.Sales+1@sub.example.com 其他文字");

        Assert.Equal(2, result.Count);
        Assert.Contains("alice@example.com", result);
        Assert.Contains("bob.sales+1@sub.example.com", result);
    }

    [Fact]
    public void Rejects_addresses_that_do_not_end_exactly_in_dot_com()
    {
        var result = WhitelistDeletionParser.ExtractComAddresses(
            "cn@example.com.cn org@example.org malformed@example.com-extra");

        Assert.Empty(result);
    }

    [Fact]
    public void Empty_text_returns_an_empty_set()
    {
        Assert.Empty(WhitelistDeletionParser.ExtractComAddresses(null));
        Assert.Empty(WhitelistDeletionParser.ExtractComAddresses("  "));
    }
}
