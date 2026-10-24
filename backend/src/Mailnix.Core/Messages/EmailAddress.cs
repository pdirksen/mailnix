using System.Globalization;

namespace Mailnix.Core.Messages;

/// <summary>A mailbox address with normalized (lowercase, IDN as punycode) address and domain for matching.</summary>
public sealed record EmailAddress
{
    private static readonly IdnMapping Idn = new();

    public EmailAddress(string address, string? name = null)
    {
        var trimmed = address.Trim();
        var at = trimmed.LastIndexOf('@');
        Domain = at < 0 ? string.Empty : NormalizeDomain(trimmed[(at + 1)..]);
        LocalPart = (at < 0 ? trimmed : trimmed[..at]).ToLowerInvariant();
        Address = Domain.Length == 0 ? LocalPart : $"{LocalPart}@{Domain}";
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>Normalized address, e.g. <c>news@xn--mller-kva.de</c>.</summary>
    public string Address { get; }

    public string LocalPart { get; }

    public string Domain { get; }

    public string? Name { get; }

    /// <summary>"shop" for "me+shop@example.com"; null without a plus tag.</summary>
    public string? PlusTag => LocalPart.IndexOf('+', StringComparison.Ordinal) is var plus and > 0 ? LocalPart[(plus + 1)..] : null;

    /// <summary>The address without its plus tag: "me@example.com" for "me+shop@example.com".</summary>
    public string BaseAddress => PlusTag is null ? Address : $"{LocalPart[..LocalPart.IndexOf('+', StringComparison.Ordinal)]}@{Domain}";

    public static string NormalizeDomain(string domain)
    {
        var lower = domain.Trim().TrimEnd('.').ToLowerInvariant();
        try
        {
            return Idn.GetAscii(lower);
        }
        catch (ArgumentException)
        {
            return lower;
        }
    }

    /// <summary>True if <paramref name="domain"/> is <paramref name="parent"/> or one of its subdomains.</summary>
    public static bool IsDomainOrSubdomain(string domain, string parent) =>
        domain.Length > 0 && parent.Length > 0
        && (domain.Equals(parent, StringComparison.Ordinal) || domain.EndsWith("." + parent, StringComparison.Ordinal));

    public override string ToString() => Name is null ? Address : $"{Name} <{Address}>";
}
