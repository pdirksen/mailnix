namespace Mailnix.Infrastructure.Data;

/// <summary>
/// Which worker processes a mailbox. A worker renews its leases while it runs; when it stops or dies, the lease
/// expires and another worker takes the mailbox over. One lease per account: never two workers on one mailbox.
/// </summary>
public class MailboxLease
{
    public Guid AccountId { get; set; }

    /// <summary>Null while the account is not leased.</summary>
    public string? WorkerId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? AcquiredAt { get; set; }
}

/// <summary>A running worker process, for the system overview (heartbeats; rows of dead workers age out).</summary>
public class WorkerInstance
{
    public required string Id { get; set; }

    public required string Host { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>
/// A live event (run progress, account status) from a worker process for the browsers connected to the API
/// processes. Every API process relays new rows to its own clients; old rows are deleted after a few minutes.
/// </summary>
public class LiveEventRecord
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    public required string Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An invitation into a tenant. The token is shown once; only its hash is stored.</summary>
public class Invitation
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Only this address may accept, when set.</summary>
    public string? Email { get; set; }

    public required string TokenHash { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public Guid? AcceptedByUserId { get; set; }
}
