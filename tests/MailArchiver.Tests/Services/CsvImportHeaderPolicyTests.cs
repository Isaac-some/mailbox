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
}
