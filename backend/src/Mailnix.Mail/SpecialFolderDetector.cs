namespace Mailnix.Mail;

/// <summary>
/// Finds Sent/Drafts/Trash/Junk/Archive. Prefers the server's SPECIAL-USE flags and falls back to well-known
/// folder names (English and German clients, Outlook/Exchange, Thunderbird, Apple Mail) for servers without them.
/// </summary>
public static class SpecialFolderDetector
{
    public sealed record Candidate(string Path, string Name, bool CanSelect, SpecialFolderKind? FlaggedAs);

    private static readonly Dictionary<SpecialFolderKind, string[]> KnownNames = new()
    {
        [SpecialFolderKind.Sent] = ["Sent", "Sent Items", "Sent Messages", "Sent Mail", "Gesendet", "Gesendete Elemente", "Gesendete Objekte"],
        [SpecialFolderKind.Drafts] = ["Drafts", "Draft", "Entwürfe", "Entwurf"],
        [SpecialFolderKind.Trash] = ["Trash", "Deleted Items", "Deleted Messages", "Bin", "Papierkorb", "Gelöschte Elemente", "Gelöschte Objekte"],
        [SpecialFolderKind.Junk] = ["Junk", "Spam", "Junk E-mail", "Junk Email", "Junk-E-Mail", "Bulk Mail"],
        [SpecialFolderKind.Archive] = ["Archive", "Archives", "Archiv"],
    };

    public static SpecialFolderMap Detect(IReadOnlyList<Candidate> folders)
    {
        string? Find(SpecialFolderKind kind)
        {
            var selectable = folders.Where(f => f.CanSelect).ToList();
            var flagged = selectable.FirstOrDefault(f => f.FlaggedAs == kind);
            if (flagged is not null)
            {
                return flagged.Path;
            }

            // Name match; the shallowest folder wins (e.g. "INBOX.Trash" over "INBOX.Old.Trash").
            return selectable
                .Where(f => KnownNames[kind].Contains(f.Name, StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f.Path.Length - f.Name.Length)
                .ThenBy(f => Array.FindIndex(KnownNames[kind], n => n.Equals(f.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(f => f.Path)
                .FirstOrDefault();
        }

        return new SpecialFolderMap(
            Sent: Find(SpecialFolderKind.Sent),
            Drafts: Find(SpecialFolderKind.Drafts),
            Trash: Find(SpecialFolderKind.Trash),
            Junk: Find(SpecialFolderKind.Junk),
            Archive: Find(SpecialFolderKind.Archive));
    }
}
