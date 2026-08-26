using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class CsvImportHeaderPolicyTests
{
    [Fact]
    public void TryCreateCanonicalIndex_accepts_the_full_Chinese_export_and_uses_SMTP_authorization_code()
    {
        var accepted = CsvImportHeaderPolicy.TryCreateCanonicalIndex(
            ["邮箱类型", "是否已使用", "序号", "邮箱", "密码", "SMTP授权码", "辅助邮箱", "2fa", "名字"],
            out var columns);

        Assert.True(accepted);
        Assert.Equal(3, columns["email"]);
        Assert.Equal(5, columns["app_password"]);
        Assert.False(columns.ContainsKey("name"));
        Assert.False(columns.ContainsKey("password"));
    }

    [Theory]
    [InlineData("邮箱", "Client ID", "Refresh Token")]
    [InlineData("email", "client_id", "client_secret", "refresh_token")]
    public void TryCreateCanonicalIndex_accepts_OAuth_headers(params string[] headers)
    {
        var accepted = CsvImportHeaderPolicy.TryCreateCanonicalIndex(headers, out var columns);

        Assert.True(accepted);
        Assert.True(columns.ContainsKey("email"));
        Assert.True(columns.ContainsKey("client_id"));
        Assert.True(columns.ContainsKey("refresh_token"));
    }

    [Fact]
    public void TryCreateCanonicalIndex_maps_Yahoo_redirect_uri()
    {
        Assert.True(CsvImportHeaderPolicy.TryCreateCanonicalIndex(
            ["邮箱", "Client ID", "Client Secret", "Refresh Token", "Redirect URI"], out var columns));
        Assert.Equal(4, columns["redirect_uri"]);
    }
}
