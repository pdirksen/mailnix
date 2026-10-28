namespace Mailnix.Infrastructure.Data;

public enum ApiTokenKind
{
    /// <summary>Created by the user in Settings, for an MCP client configured by hand.</summary>
    Personal,

    /// <summary>An app the user connected through OAuth (e.g. a Claude.ai connector); has a refresh token.</summary>
    Connection,
}

public enum ApiScope
{
    /// <summary>Reading accounts, rules, previews, stats and runs.</summary>
    Read,

    /// <summary>Also drafting rules (disabled or dry run), starting runs and undo.</summary>
    ReadWrite,
}

/// <summary>
/// Access to the MCP endpoint for one user: a personal access token or an OAuth connection. Only hashes of the
/// tokens are stored. Revoking deletes the row.
/// </summary>
public class ApiToken
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public ApiTokenKind Kind { get; set; }

    /// <summary>The token's name, or the connected app's name.</summary>
    public required string Name { get; set; }

    public ApiScope Scope { get; set; }

    /// <summary>Connections: the app they belong to.</summary>
    public Guid? ClientId { get; set; }

    /// <summary>The personal token, or the connection's current access token.</summary>
    public required string TokenHash { get; set; }

    /// <summary>The start of a personal token, to recognize it in the list.</summary>
    public string? TokenHint { get; set; }

    /// <summary>When the (access) token stops working; null for personal tokens without expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Connections: the current refresh token (replaced on every use).</summary>
    public string? RefreshTokenHash { get; set; }

    public DateTimeOffset? RefreshExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>An app that registered itself for OAuth (dynamic client registration). Public client: no secret.</summary>
public class ConnectedApp
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Https addresses, or loopback addresses of desktop apps.</summary>
    public List<string> RedirectUris { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An authorization code: single use, valid for a few minutes, bound to the PKCE challenge.</summary>
public class AuthorizationCode
{
    public Guid Id { get; set; }

    public required string CodeHash { get; set; }

    public Guid ClientId { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public required string RedirectUri { get; set; }

    /// <summary>S256 challenge (base64url of the verifier's SHA-256).</summary>
    public required string CodeChallenge { get; set; }

    public ApiScope Scope { get; set; }

    /// <summary>The resource (MCP endpoint) the client asked for, if it named one (RFC 8707).</summary>
    public string? Resource { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
