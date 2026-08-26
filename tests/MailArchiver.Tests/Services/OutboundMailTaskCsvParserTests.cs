using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class OutboundMailTaskCsvParserTests
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    [Fact]
    public void Parse_accepts_chinese_headers_and_preserves_quoted_multiline_body()
    {
        using var reader = new StringReader(
            "时间,发件邮箱,收件邮箱,主题,正文\r\n" +
            "2026-08-24 15:30:00,sender@outlook.com,target@gmx.com,测试 01,\"第一行,有逗号\r\n第二行\"\r\n");

        var result = OutboundMailTaskCsvParser.Parse(
            reader,
            new DateTime(2026, 8, 24, 1, 0, 0, DateTimeKind.Utc),
            Shanghai);

        var row = Assert.Single(result.Rows);
        Assert.Empty(result.Errors);
        Assert.Equal(new DateTime(2026, 8, 24, 7, 30, 0, DateTimeKind.Utc), row.ScheduledAtUtc);
        Assert.Equal("sender@outlook.com", row.Sender);
        Assert.Equal("target@gmx.com", row.Recipient);
        Assert.Equal("第一行,有逗号\r\n第二行", row.Body);
    }

    [Fact]
    public void Parse_uses_now_for_blank_time_and_accepts_english_aliases()
    {
        var now = new DateTime(2026, 8, 24, 7, 30, 0, DateTimeKind.Utc);
        using var reader = new StringReader(
            "scheduledAt,from,to,subject,body\n" +
            ",sender@outlook.com,target@gmx.com,Subject,Body\n");

        var result = OutboundMailTaskCsvParser.Parse(reader, now, Shanghai);

        Assert.Equal(now, Assert.Single(result.Rows).ScheduledAtUtc);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_rejects_missing_headers_before_reading_rows()
    {
        using var reader = new StringReader(
            "发件邮箱,收件邮箱,主题\n" +
            "sender@outlook.com,target@gmx.com,Subject\n");

        var result = OutboundMailTaskCsvParser.Parse(reader, DateTime.UtcNow, Shanghai);

        Assert.Empty(result.Rows);
        var error = Assert.Single(result.Errors);
        Assert.Contains("时间", error.Reason);
        Assert.Contains("正文", error.Reason);
    }

    [Theory]
    [InlineData("bad-time,sender@outlook.com,target@gmx.com,Subject,Body", "时间格式")]
    [InlineData(",not-an-email,target@gmx.com,Subject,Body", "发件邮箱格式")]
    [InlineData(",sender@outlook.com,not-an-email,Subject,Body", "收件邮箱格式")]
    [InlineData(",sender@outlook.com,target@gmx.com,,Body", "主题不能为空")]
    [InlineData(",sender@outlook.com,target@gmx.com,Subject,", "正文不能为空")]
    public void Parse_reports_invalid_rows(string row, string expectedReason)
    {
        using var reader = new StringReader("时间,发件邮箱,收件邮箱,主题,正文\n" + row + "\n");

        var result = OutboundMailTaskCsvParser.Parse(reader, DateTime.UtcNow, Shanghai);

        Assert.Empty(result.Rows);
        Assert.Contains(expectedReason, Assert.Single(result.Errors).Reason);
    }
}
