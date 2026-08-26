using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MailArchiver.ViewModels;

public sealed class ComposeMailViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "请选择发件账号。")]
    public int AccountId { get; set; }

    [Required(ErrorMessage = "请填写收件人。")]
    public string To { get; set; } = string.Empty;

    public string Cc { get; set; } = string.Empty;

    [Required(ErrorMessage = "请填写主题。")]
    [StringLength(998, ErrorMessage = "主题过长。")]
    public string Subject { get; set; } = string.Empty;

    [Required(ErrorMessage = "请填写正文。")]
    public string Body { get; set; } = string.Empty;

    public List<IFormFile> Attachments { get; set; } = new();

    public List<SelectListItem> SendingAccounts { get; set; } = new();
    public int MaxAttachmentCount { get; set; }
    public long MaxTotalAttachmentBytes { get; set; }
}
