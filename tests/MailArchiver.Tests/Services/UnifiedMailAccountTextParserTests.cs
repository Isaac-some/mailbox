using MailArchiver.Models;
using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class UnifiedMailAccountTextParserTests
{
    [Fact]
    public void Parse_detects_each_provider_independently_in_one_mixed_file()
    {
        const string clientId = "11111111-2222-3333-4444-555555555555";
        using var reader = new StringReader(
            "first@yahoo.com\tyahoo-password\n" +
            $"second@outlook.com\tignored-password\t{clientId}\toutlook-refresh\n" +
            "third@gmx.de\tgmx-password\n" +
            "fourth@gmail.com\tgoogle-client\tgoogle-refresh\n");

        var result = UnifiedMailAccountTextParser.Parse(reader);

        Assert.Empty(result.Errors);
        Assert.Collection(result.Accounts,
            account => Assert.Equal(MailProviderKind.Yahoo, account.Provider),
            account => Assert.Equal(MailProviderKind.Outlook, account.Provider),
            account => Assert.Equal(MailProviderKind.Gmx, account.Provider),
            account => Assert.Equal(MailProviderKind.Gmail, account.Provider));
        Assert.Null(result.Accounts[1].AppPassword);
    }

    [Fact]
    public void Parse_keeps_both_credentials_for_receive_and_send_fallback()
    {
        using var reader = new StringReader(
            "both@gmail.com\tabcd efgh ijkl mnop\tgoogle-client\t\tgoogle-refresh\t\n" +
            "both@yahoo.com\tyahoo-password\tyahoo-client\tyahoo-secret\tyahoo-refresh\toob\n");

        var result = UnifiedMailAccountTextParser.Parse(reader);

        Assert.Empty(result.Errors);
        Assert.All(result.Accounts, account =>
        {
            Assert.False(string.IsNullOrWhiteSpace(account.AppPassword));
            Assert.False(string.IsNullOrWhiteSpace(account.ClientId));
            Assert.False(string.IsNullOrWhiteSpace(account.RefreshToken));
        });
    }

    [Theory]
    [InlineData("person@example.com\tpassword")]
    [InlineData("person@gmx.com\tclient\trefresh")]
    [InlineData("person@outlook.com\tpassword\tnot-a-guid\trefresh")]
    public void Parse_rejects_unsupported_or_incomplete_rows_without_affecting_other_rows(string badRow)
    {
        using var reader = new StringReader(badRow + "\nvalid@gmx.com\tvalid-password\n");

        var result = UnifiedMailAccountTextParser.Parse(reader);

        Assert.Single(result.Accounts);
        Assert.Single(result.Errors);
        Assert.Equal(2, result.Accounts[0].LineNumber);
    }
}
