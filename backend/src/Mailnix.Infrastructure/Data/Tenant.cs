namespace Mailnix.Infrastructure.Data;

/// <summary>
/// An isolated customer space. Self-hosted installations only ever use <see cref="DefaultId"/>;
/// SaaS creates one tenant per signup.
/// </summary>
public class Tenant
{
    public static readonly Guid DefaultId = new("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
