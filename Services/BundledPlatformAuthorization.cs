namespace MailArchiver.Services;

// Release builds can supply a private generated implementation. No tenant
// credential is kept in source control or in the distributed settings file.
internal static partial class BundledPlatformAuthorization
{
    public static void Apply(IConfiguration configuration) => ApplyReleaseToken(configuration);
    static partial void ApplyReleaseToken(IConfiguration configuration);
}
