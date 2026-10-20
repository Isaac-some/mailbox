#!/usr/bin/env python3
"""Generate private release source; never print the token or put it in arguments."""
import base64
import os
import pathlib
import sys

target = pathlib.Path(sys.argv[1])
token = os.environ.get("MAIL_ASSISTANT_UPSTREAM_TOKEN", "").strip()
if not token:
    raise SystemExit("Missing release token")
encoded = base64.b64encode(token.encode()).decode("ascii")
target.write_text('''namespace MailArchiver.Services;
internal static partial class BundledPlatformAuthorization
{
    static partial void ApplyReleaseToken(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["UpstreamMailboxSync:BearerToken"]))
            configuration["UpstreamMailboxSync:BearerToken"] =
                System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String("''' + encoded + '''"));
    }
}
''')
target.chmod(0o600)
