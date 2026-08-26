using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class ExternalMailAccountTextParserTests
{
    [Fact]
    public void Parse_accepts_password_rows_for_Gmail_Yahoo_and_GMX()
    {
        using var reader = new StringReader(
            "one@gmail.com\tgmail-app-password\n" +
            "two@yahoo.com\tyahoo-app-password\n" +
            "three@gmx.com\tgmx-app-password\n");

        var result = ExternalMailAccountTextParser.Parse(reader);

        Assert.Equal(3, result.Accounts.Count);
        Assert.Empty(result.Errors);
        Assert.All(result.Accounts, account => Assert.False(string.IsNullOrWhiteSpace(account.AppPassword)));
    }

    [Fact]
    public void Parse_accepts_Gmail_OAuth_without_secret_and_Yahoo_with_secret()
    {
        using var reader = new StringReader(
            "one@gmail.com\tgoogle-client\tgoogle-refresh\n" +
            "two@yahoo.com\tyahoo-client\tyahoo-secret\tyahoo-refresh\toob\n");

        var result = ExternalMailAccountTextParser.Parse(reader);

        Assert.Equal(2, result.Accounts.Count);
        Assert.Empty(result.Errors);
        Assert.Null(result.Accounts[0].ClientSecret);
        Assert.Equal("yahoo-secret", result.Accounts[1].ClientSecret);
        Assert.Equal("oob", result.Accounts[1].RedirectUri);
    }

    [Theory]
    [InlineData("two@yahoo.com\tyahoo-client\tyahoo-refresh")]
    [InlineData("three@gmx.com\tgmx-client\tgmx-refresh")]
    [InlineData("person@example.com\tpassword")]
    public void Parse_rejects_unsupported_OAuth_or_provider_rows(string line)
    {
        using var reader = new StringReader(line);

        var result = ExternalMailAccountTextParser.Parse(reader);

        Assert.Empty(result.Accounts);
        Assert.Single(result.Errors);
    }
}
