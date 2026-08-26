using System.ComponentModel.DataAnnotations;

namespace MailArchiver.ViewModels;

public sealed class RegisterViewModel
{
    [Required(ErrorMessage = "请输入授权码")]
    [Display(Name = "授权码")]
    public string AuthorizationCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入用户名")]
    [StringLength(50, MinimumLength = 3, ErrorMessage = "用户名长度应为 3 到 50 个字符")]
    [Display(Name = "用户名")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入邮箱")]
    [EmailAddress(ErrorMessage = "请输入有效的邮箱地址")]
    [StringLength(100)]
    [Display(Name = "邮箱")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入密码")]
    [DataType(DataType.Password)]
    [StringLength(100, MinimumLength = 10, ErrorMessage = "密码至少需要 10 个字符")]
    [Display(Name = "密码")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "请再次输入密码")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "两次输入的密码不一致")]
    [Display(Name = "确认密码")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
