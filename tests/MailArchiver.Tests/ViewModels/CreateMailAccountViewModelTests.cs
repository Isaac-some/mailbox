using System.ComponentModel.DataAnnotations;
using MailArchiver.Models;
using MailArchiver.Models.ViewModels;

namespace MailArchiver.Tests.ViewModels;

public class CreateMailAccountViewModelTests
{
    [Fact]
    public void Gmail_OAuth_is_valid_without_an_app_password()
    {
        var model = OAuthModel("reader@gmail.com", clientSecret: null);

        var errors = Validate(model);

        Assert.DoesNotContain(errors, error => error.MemberNames.Contains(nameof(model.Password)));
    }

    [Fact]
    public void Yahoo_OAuth_requires_client_secret()
    {
        var model = OAuthModel("reader@yahoo.com", clientSecret: null);

        var errors = Validate(model);

        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(model.Password)));
    }

    private static CreateMailAccountViewModel OAuthModel(string email, string? clientSecret)
        => new()
        {
            Provider = ProviderType.IMAP,
            Name = "reader",
            EmailAddress = email,
            ImapServer = "fixed-by-controller",
            ImapPort = 993,
            Username = email,
            ExternalOAuthClientId = "client-id",
            ExternalOAuthClientSecret = clientSecret,
            ExternalOAuthRedirectUri = email.Contains("yahoo", StringComparison.OrdinalIgnoreCase) ? "oob" : null,
            OAuthRefreshToken = "refresh-token"
        };

    private static List<ValidationResult> Validate(CreateMailAccountViewModel model)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), errors, validateAllProperties: true);
        return errors;
    }
}
