using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class MailCredentialInputPolicyTests
{
    [Theory]
    [InlineData(" abcd efgh ijkl mnop ", "abcdefghijklmnop")]
    [InlineData("Ab\tC\r\nd\u3000E\u00A0f\u200Bg\uFEFFh", "AbCdEfgh")]
    [InlineData(" 0.Abc_d-E+/=.!@# ", "0.Abc_d-E+/=.!@#")]
    public void Copy_artifacts_are_removed_without_changing_case_or_symbols(string input, string expected)
        => Assert.Equal(expected, MailCredentialInputPolicy.NormalizeAndValidate(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n\u3000\u200B\uFEFF")]
    [InlineData("abc\0def")]
    [InlineData("abc\u007Fdef")]
    public void Invalid_content_is_rejected_without_echoing_the_credential(string? input)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => MailCredentialInputPolicy.NormalizeAndValidate(input));
        Assert.DoesNotContain("abc", exception.Message);
    }

    [Fact]
    public void Oversized_input_is_rejected()
        => Assert.Throws<InvalidOperationException>(() =>
            MailCredentialInputPolicy.NormalizeAndValidate(new string('a', MailCredentialInputPolicy.MaxCredentialLength + 1)));
}
