using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class MailAccountNamePolicyTests
{
    [Theory]
    [InlineData("laurathomas519657@yahoo.com", "laurathomas")]
    [InlineData("ZajSheridan_lindsayq@yahoo.com", "ZajSheridan_lindsayq")]
    [InlineData("Zmabraham_zoraFSWe1@yahoo.com", "Zmabraham_zoraFSWe")]
    [InlineData("zailanijoe345356@yahoo.com", "zailanijoe")]
    [InlineData("Jody.Gloverr@gmx.com", "Jody.Gloverr")]
    public void Derive_uses_the_non_numeric_part_before_at(string email, string expected)
    {
        Assert.Equal(expected, MailAccountNamePolicy.Derive(email));
    }

    [Fact]
    public void Derive_keeps_numeric_local_part_as_a_non_empty_fallback()
    {
        Assert.Equal("12345", MailAccountNamePolicy.Derive("12345@yahoo.com"));
    }
}
