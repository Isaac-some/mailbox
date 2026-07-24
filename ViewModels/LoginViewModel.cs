using System.ComponentModel.DataAnnotations;

namespace MailArchiver.Models.ViewModels
{
    public class LoginViewModel
    {
        [Required(ErrorMessage = "请输入用户名")]
        [Display(Name = "Username")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "请输入密码")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "记住我")]
        public bool RememberMe { get; set; }
    }
}
