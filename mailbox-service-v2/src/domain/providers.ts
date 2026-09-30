import { z } from "zod";

export const providers = ["gmail", "outlook", "qq", "163", "custom"] as const;
export type Provider = typeof providers[number];

export const providerDefaults: Record<Exclude<Provider, "custom">, { imapHost: string; imapPort: number; smtpHost: string; smtpPort: number; smtpSecure: boolean }> = {
  gmail: { imapHost: "imap.gmail.com", imapPort: 993, smtpHost: "smtp.gmail.com", smtpPort: 465, smtpSecure: true },
  outlook: { imapHost: "outlook.office365.com", imapPort: 993, smtpHost: "smtp.office365.com", smtpPort: 587, smtpSecure: false },
  qq: { imapHost: "imap.qq.com", imapPort: 993, smtpHost: "smtp.qq.com", smtpPort: 465, smtpSecure: true },
  "163": { imapHost: "imap.163.com", imapPort: 993, smtpHost: "smtp.163.com", smtpPort: 465, smtpSecure: true }
};

export const accountInputSchema = z.object({
  address: z.string().email().transform((v) => v.trim().toLowerCase()),
  displayName: z.string().trim().max(120).default(""),
  provider: z.enum(providers),
  imapHost: z.string().trim().max(253).optional(),
  imapPort: z.coerce.number().int().min(1).max(65535).optional(),
  imapSecure: z.boolean().optional(),
  smtpHost: z.string().trim().max(253).optional(),
  smtpPort: z.coerce.number().int().min(1).max(65535).optional(),
  smtpSecure: z.boolean().optional(),
  credential: z.discriminatedUnion("type", [
    z.object({ type: z.literal("password"), username: z.string().min(1).max(320), secret: z.string().min(1).max(4096) }),
    z.object({
      type: z.literal("oauth_refresh"), username: z.string().min(1).max(320), refreshToken: z.string().min(10).max(8192),
      clientId: z.string().min(1).max(1024), clientSecret: z.string().max(4096).optional(), tokenEndpoint: z.string().url()
    })
  ])
}).superRefine((value, ctx) => {
  if (value.provider === "custom" && (!value.imapHost || !value.smtpHost)) {
    ctx.addIssue({ code: "custom", message: "自定义邮箱必须填写 IMAP 和 SMTP 地址" });
  }
  if (value.credential.type === "oauth_refresh") {
    const host = new URL(value.credential.tokenEndpoint).hostname.toLowerCase();
    const allowed = value.provider === "gmail"
      ? ["oauth2.googleapis.com"]
      : value.provider === "outlook"
        ? ["login.microsoftonline.com", "login.live.com"]
        : [];
    if (!allowed.includes(host)) ctx.addIssue({ code: "custom", path: ["credential", "tokenEndpoint"], message: "OAuth token endpoint is not allowed for this provider" });
  }
});

export function resolveEndpoints(input: z.infer<typeof accountInputSchema>) {
  const defaults = input.provider === "custom" ? undefined : providerDefaults[input.provider];
  return {
    imapHost: input.imapHost ?? defaults?.imapHost ?? "",
    imapPort: input.imapPort ?? defaults?.imapPort ?? 993,
    imapSecure: input.imapSecure ?? true,
    smtpHost: input.smtpHost ?? defaults?.smtpHost ?? "",
    smtpPort: input.smtpPort ?? defaults?.smtpPort ?? 465,
    smtpSecure: input.smtpSecure ?? defaults?.smtpSecure ?? true
  };
}
