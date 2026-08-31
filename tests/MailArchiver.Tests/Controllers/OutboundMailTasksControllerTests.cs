using System.Text;
using MailArchiver.Controllers;
using MailArchiver.Models;
using MailArchiver.Services;

namespace MailArchiver.Tests.Controllers;

public sealed class OutboundMailTasksControllerTests
{
    [Fact]
    public void BuildDownloadTemplate_ProducesImportableFutureScheduleRows()
    {
        var utcNow = new DateTime(2026, 8, 31, 8, 0, 45, DateTimeKind.Utc);
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+08-test",
            TimeSpan.FromHours(8),
            "UTC+08-test",
            "UTC+08-test");

        var bytes = OutboundMailTasksController.BuildDownloadTemplate(utcNow, timeZone);

        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
        var parsed = OutboundMailTaskCsvParser.Parse(
            reader,
            utcNow,
            timeZone,
            OutboundMailTaskTimingMode.UseCsvSchedule);
        Assert.Empty(parsed.Errors);
        Assert.Equal(2, parsed.Rows.Count);
        Assert.All(parsed.Rows, row => Assert.True(row.ScheduledAtUtc > utcNow));
    }
}
