using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class OutlookAccountTextParserTests
{
    [Fact]
    public void Parse_accepts_the_vendor_tab_format_without_exposing_the_password()
    {
        var clientId = "11111111-2222-3333-4444-555555555555";
        var refreshToken = "M.C.A-b_c*token!" + new string('x', 465);
        using var reader = new StringReader(
            $"buyer123@outlook.com\tPassword123!\t{clientId}\t{refreshToken}\n");

        var result = OutlookAccountTextParser.Parse(reader);

        var account = Assert.Single(result.Accounts);
        Assert.Empty(result.Errors);
        Assert.Equal(1, account.LineNumber);
        Assert.Equal("buyer123@outlook.com", account.Email);
        Assert.Equal(clientId, account.ClientId);
        Assert.Equal(refreshToken, account.RefreshToken);
        Assert.DoesNotContain(
            typeof(OutlookImportedAccount).GetProperties(),
            property => property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));

        var mailAccount = OutlookImportedAccountFactory.Create(account, isEnabled: true);
        Assert.Equal(MailArchiver.Models.ProviderType.MSA, mailAccount.Provider);
        Assert.Equal("outlook.office365.com", mailAccount.ImapServer);
        Assert.Equal(clientId, mailAccount.ClientId);
        Assert.Equal(refreshToken, mailAccount.OAuthRefreshToken);
        Assert.Null(mailAccount.Password);
        Assert.Null(mailAccount.OAuthGrantedScopes);
    }

    [Theory]
    [InlineData("not-an-email\tPassword123!\t11111111-2222-3333-4444-555555555555\trefresh-token")]
    [InlineData("buyer@outlook.com\tPassword123!\tnot-a-client-id\trefresh-token")]
    [InlineData("buyer@outlook.com\tPassword123!\t11111111-2222-3333-4444-555555555555\t")]
    [InlineData("buyer@outlook.com,Password123!,11111111-2222-3333-4444-555555555555,refresh-token")]
    public void Parse_rejects_malformed_rows(string line)
    {
        using var reader = new StringReader(line);

        var result = OutlookAccountTextParser.Parse(reader);

        Assert.Empty(result.Accounts);
        Assert.Single(result.Errors);
    }
}
