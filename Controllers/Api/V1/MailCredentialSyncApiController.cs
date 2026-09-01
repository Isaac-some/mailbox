using MailArchiver.Controllers.Api;
using MailArchiver.Models.Api;
using MailArchiver.Services;
using Microsoft.AspNetCore.Mvc;

namespace MailArchiver.Controllers.Api.V1;

[Route("api/v1/mailbox-credentials")]
[RequestSizeLimit(2_000_000)]
public sealed class MailCredentialSyncApiController : ApiControllerBase
{
    private readonly MailCredentialIntakeService _intake;

    public MailCredentialSyncApiController(MailCredentialIntakeService intake)
    {
        _intake = intake;
    }

    /// <summary>
    /// Accepts upstream batches in the minimal format: email, credential, optional domain.
    /// The endpoint is intentionally batch-limited so a large upstream dataset can be
    /// streamed page by page without materializing every mailbox in memory.
    /// </summary>
    [HttpPost("sync")]
    public async Task<ActionResult<List<MailCredentialSyncItemResult>>> Sync(
        [FromBody] MailCredentialSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.Items is null || request.Items.Count == 0)
            return BadRequest(new { message = "Items 不能为空。" });
        if (request.Items.Count > 500)
            return BadRequest(new { message = "单批最多提交 500 个邮箱，请分批调用。" });
        if (!User.IsInRole("Admin") && !User.IsInRole("SelfManager"))
            return Forbid();

        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(userIdClaim, out var userId))
            return Unauthorized();

        var results = new List<MailCredentialSyncItemResult>(request.Items.Count);
        foreach (var item in request.Items)
        {
            if (item is null)
            {
                results.Add(new MailCredentialSyncItemResult
                {
                    Status = "Rejected",
                    Error = "邮箱条目不能为空。"
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Email) || string.IsNullOrWhiteSpace(item.Credential))
            {
                results.Add(new MailCredentialSyncItemResult
                {
                    Email = item.Email ?? string.Empty,
                    Status = "Rejected",
                    Error = "邮箱和授权凭据不能为空。"
                });
                continue;
            }

            try
            {
                var result = await _intake.UpsertAsync(
                    userId,
                    new MailCredentialIntake(item.Email, item.Credential, item.Domain, item.ClientId),
                    request.IsEnabled,
                    cancellationToken);
                results.Add(new MailCredentialSyncItemResult
                {
                    Email = result.Account.EmailAddress,
                    Created = result.Created,
                    CredentialKind = result.Kind.ToString(),
                    CredentialScope = result.Scope.ToString(),
                    Status = result.Status
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                results.Add(new MailCredentialSyncItemResult
                {
                    Email = item.Email,
                    Status = "Rejected",
                    Error = ex.Message
                });
            }
        }

        return Ok(results);
    }
}
