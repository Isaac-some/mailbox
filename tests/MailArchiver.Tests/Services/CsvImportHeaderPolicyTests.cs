using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class CsvImportHeaderPolicyTests
{
    [Fact]
    public void Accepts_the_canonical_four_column_interface_contract()
    {
        var accepted = CsvImportHeaderPolicy.TryCreateCanonicalIndex(
            ["email", "domain", "credential", "client_id"], out var columns);

        Assert.True(accepted);
        Assert.Equal(0, columns["email"]);
        Assert.Equal(1, columns["domain"]);
        Assert.Equal(2, columns["credential"]);
        Assert.Equal(3, columns["client_id"]);
    }

    [Fact]
    public void Accepts_the_same_four_fields_with_concise_Chinese_labels()
    {
        var accepted = CsvImportHeaderPolicy.TryCreateCanonicalIndex(
            ["邮箱", "域名", "授权码", "Cilent ID"], out var columns);

        Assert.True(accepted);
        Assert.Equal(2, columns["credential"]);
        Assert.Equal(3, columns["client_id"]);
    }

    [Theory]
    [InlineData("email,credential,domain,client_id,notes")]
    [InlineData("email,credential,domain")]
    [InlineData("email,domain,refresh_token,client_id")]
    [InlineData("email,domain,credential,credential")]
    public void Rejects_schema_drift(string headerLine)
    {
        var accepted = CsvImportHeaderPolicy.TryCreateCanonicalIndex(
            headerLine.Split(','), out _);

        Assert.False(accepted);
    }

    [Fact]
    public void Flexible_mode_accepts_screenshot_style_headers_and_prefers_imap_code()
    {
        var accepted = CsvImportHeaderPolicy.TryCreateFlexibleIndex(
            ["新增日期", "邮箱类型", "是否已使用", "序号", "邮箱", "密码", "IMAP授权码", "2fa"],
            out var columns);

        Assert.True(accepted);
        Assert.Equal(4, columns["email"]);
        Assert.Equal(6, columns["credential"]);
    }

    [Fact]
    public void Flexible_mode_allows_extra_columns_and_english_aliases()
    {
        var accepted = CsvImportHeaderPolicy.TryCreateFlexibleIndex(
            ["date", "email address", "password", "notes", "domain"],
            out var columns);

        Assert.True(accepted);
        Assert.Equal(1, columns["email"]);
        Assert.Equal(2, columns["credential"]);
        Assert.Equal(4, columns["domain"]);
    }

    [Fact]
    public void Flexible_mode_accepts_the_supplied_yahoo_and_gmx_export_shape()
    {
        var accepted = CsvImportHeaderPolicy.TryCreateFlexibleIndex(
            ["邮箱类型", "是否已使用", "序号", "邮箱", "密码", "SMTP授权码", "辅助邮箱", "名字", "分组"],
            out var columns);

        Assert.True(accepted);
        Assert.Equal(3, columns["email"]);
        Assert.Equal(5, columns["credential"]);
    }

}
