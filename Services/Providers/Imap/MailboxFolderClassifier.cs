using MailArchiver.Models;
using MailKit;

namespace MailArchiver.Services.Providers.Imap;

public static class MailboxFolderClassifier
{
    private static readonly Dictionary<MailboxFolderCategory, HashSet<string>> KnownNames = new()
    {
        [MailboxFolderCategory.Inbox] = Names("inbox", "收件箱", "boite de reception", "boite de réception", "posteingang"),
        [MailboxFolderCategory.Sent] = Names(
            "sent", "sent items", "sent mail", "已发送", "已传送", "送信済み", "送信済メール", "送信メール",
            "gesendet", "gesendete objekte", "envoye", "envoyé", "elements envoyes", "éléments envoyés",
            "enviado", "elementos enviados", "verzonden", "posta inviata", "inviato", "보낸편지함"),
        [MailboxFolderCategory.Junk] = Names(
            "bulk", "junk", "junk email", "junk e-mail", "spam", "spamverdacht", "courrier indesirable",
            "courrier indésirable", "correo no deseado", "unerwunscht", "unerwünscht", "迷惑メール", "垃圾邮件"),
        [MailboxFolderCategory.Trash] = Names(
            "trash", "deleted", "deleted items", "bin", "papierkorb", "gelöscht", "geloescht", "geloscht",
            "corbeille", "elementos eliminados",
            "已删除", "已删除邮件", "削除済み", "ゴミ箱"),
        [MailboxFolderCategory.Drafts] = Names(
            "draft", "drafts", "entwurfe", "entwürfe", "brouillons", "bozze", "草稿", "下書き"),
        [MailboxFolderCategory.Archive] = Names(
            "archive", "archives", "archiv", "archivo", "归档", "アーカイブ")
    };

    public static MailboxFolderCategory Classify(
        string? name,
        string? fullName,
        FolderAttributes attributes = FolderAttributes.None)
    {
        if (attributes.HasFlag(FolderAttributes.Inbox)) return MailboxFolderCategory.Inbox;
        if (attributes.HasFlag(FolderAttributes.Sent)) return MailboxFolderCategory.Sent;
        if (attributes.HasFlag(FolderAttributes.Junk)) return MailboxFolderCategory.Junk;
        if (attributes.HasFlag(FolderAttributes.Trash)) return MailboxFolderCategory.Trash;
        if (attributes.HasFlag(FolderAttributes.Drafts)) return MailboxFolderCategory.Drafts;
        if (attributes.HasFlag(FolderAttributes.Archive)) return MailboxFolderCategory.Archive;

        var candidates = new[] { Normalize(name), Normalize(GetLeafName(fullName)) }
            .Where(value => !string.IsNullOrEmpty(value));
        foreach (var candidate in candidates)
        {
            foreach (var pair in KnownNames)
            {
                if (pair.Value.Contains(candidate!))
                    return pair.Key;
            }
        }

        return MailboxFolderCategory.Other;
    }

    public static MailboxFolderCategory Classify(string? folderName) =>
        Classify(GetLeafName(folderName), folderName);

    private static HashSet<string> Names(params string[] names) =>
        new(names.Select(Normalize).OfType<string>(), StringComparer.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? GetLeafName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return fullName;

        var separatorIndex = Math.Max(fullName.LastIndexOf('/'), fullName.LastIndexOf('.'));
        return separatorIndex >= 0 ? fullName[(separatorIndex + 1)..] : fullName;
    }
}
