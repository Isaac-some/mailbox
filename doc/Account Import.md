# Account Import

[← Back to Documentation Index](Index.md)

## Standard four-field contract

CSV import and the upstream account interface share one fixed contract. The
field names contain no spaces and must remain in this order:

| Field | Required | Meaning |
| --- | --- | --- |
| `email` | Yes | Mailbox address. |
| `domain` | No | Provider/domain supplied by the platform; it may be empty. |
| `credential` | Yes | Opaque authorization value. It may be an app password, an IMAP/SMTP password, or an OAuth2 refresh token. |
| `client_id` | No | OAuth client ID when the upstream platform has one. It may be empty. |

The standard CSV header is exactly:

```csv
email,domain,credential,client_id
```

Example:

```csv
email,domain,credential,client_id
alice@gmail.com,gmail.com,xxxx xxxx xxxx xxxx,
reader@yahoo.com,yahoo.com,upstream-authorization-value,
sender@gmx.com,gmx.com,upstream-authorization-value,
both@outlook.com,outlook.com,oauth-refresh-token,11111111-2222-3333-4444-555555555555
```

The importer also recognizes the corresponding short Chinese headers and the
legacy misspelling `Cilent ID`, but every newly generated file and every new
integration must use the canonical English names above.

## Import behavior

- Import accepts CSV only. Several CSV files may be selected at once.
- Intake removes whitespace (including tabs, newlines, full-width and nonbreaking
  spaces) and invisible Unicode format characters from generated authorization
  values. Case and punctuation are preserved. Empty results, abnormal control
  characters and values over 16,384 characters are rejected before connection.
- The app then verifies incoming mailbox login with a 20-second timeout per row.
  It uses the provider authentication funnel rather than guessing a token type.
  It does not select a mail folder, fetch messages or send mail.
- Only successful rows are stored. A rejected replacement leaves the existing
  account unchanged; other successful rows in the batch can still be imported.
  A timeout/network error is reported separately from invalid login.
- A Gmail app password may be pasted as `xxxx xxxx xxxx xxxx`; spaces are removed
  before storage and comparison, treating it as 16 characters.
- If the same email appears more than once in one batch, the last row wins.
- If the email already exists, all four imported fields replace the previous
  values. If the credential, domain or client ID changed, previous route
  selection and short-lived OAuth access-token data are cleared. Identical
  repeated input preserves the successful route and provider-rotated tokens.
- Import never starts mailbox synchronization. Its `IncomingVerified` result
  proves incoming login at that time only, not outgoing permission. Opening or
  refreshing a mailbox downloads messages later.

## Authentication funnel

The imported `credential` is made available to both password and OAuth routes.
The incoming login check at import tries the provider's default order:

- Outlook: OAuth2 first, then password-based IMAP/SMTP.
- Gmail: app password first, then OAuth2.
- Yahoo and GMX: IMAP/SMTP password first, then OAuth2.
- Custom domains: IMAP/SMTP password first, then OAuth2 where configured.

When a route succeeds, the app remembers the working incoming and outgoing
authentication methods independently and tries those methods first next time.
Only after all usable routes fail does the app report a connection failure.

## Pulling accounts from an upstream platform

When `UpstreamMailboxSync:Enabled` is enabled, every user-triggered mailbox
sync first performs an HTTPS `GET` to `UpstreamMailboxSync:Endpoint`. The
endpoint must return the documented `{ data: { total, items, serverTime } }`
envelope. The client sends `Authorization: Bearer <secret>` by default; the
platform must validate that secret before returning any credentials. A public
GitHub Release must never contain the secret in `appsettings.json` or another
bundled file.

```json
{
  "data": {
    "total": 1,
    "serverTime": "2026-09-03T08:30:00.000Z",
    "items": [
      {
        "email": "both@outlook.com",
        "domain": "outlook.com",
        "credential": "oauth-refresh-token",
        "client_id": "11111111-2222-3333-4444-555555555555",
        "updatedAt": "2026-09-02T09:30:00.000Z"
      }
    ]
  }
}
```

Imported or updated accounts are enabled locally.
Rows omitted by the upstream response are not deleted. If the upstream request
fails, the requested mailbox sync does not continue with possibly stale account
credentials.

If any returned row is rejected, successfully validated rows remain saved, but
the requested mail sync stops and reports the rejected count. Missing accounts
are not deleted. The HTTP timeout bounds fetching the platform payload; each
subsequent mailbox login has its own 20-second limit. Start integration with a
small batch: this version validates rows sequentially and does not automatically
follow platform pagination.

In the packaged desktop app, the login screen is mandatory. Enter the platform
username and password; the app sends them to `/api/auth/login` and keeps the
returned session only in process memory. No platform password or token is
written to the app bundle or local data. Restarting the app or disabling the
platform account requires another login.

Each request also includes the installation ID, device name, operating system,
and app version in `X-Kouzi-*` headers. The platform can combine these values
with the source IP and the person bound to the token when investigating use.
The client does not claim GPS or physical-location accuracy.

`serverTime` is stored in `upstream-mailbox-sync.cursor` and sent as
`updatedSince` on the next pull. The cursor advances only when every returned
row passes validation, so a bad row cannot be skipped permanently. Changing or
removing the platform connection resets the cursor and makes the next pull a
full pull.

## Result and limits

The result page separately reports created, updated, skipped, and failed rows.
Skipped rows are earlier duplicates superseded by a later row in the same
batch; an existing database account is counted as updated, not skipped.

`CsvImport:MaxFileSizeBytes` limits each uploaded file (10 MB by default).
`CsvImport:MaxRows` can impose a batch row cap; `0` means no artificial cap.

CSV files contain credentials in plaintext. Store and transfer them securely,
then delete temporary copies after a successful import.

## Microsoft 365 tenant import

Microsoft 365 tenant discovery remains a separate flow because one Azure app
registration can discover many tenant mailboxes. See
[Microsoft 365 Tenant Mailbox Import](M365TenantImport.md).
