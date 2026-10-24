using System.Security.Claims;
using System.Security.Cryptography;
using Mailnix.Api.Hosting;
using Mailnix.Api.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Accounts;

internal static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/accounts").WithTags("Accounts");

        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapDelete("/{id:guid}", Delete);

        // Everything below opens IMAP connections on the user's behalf.
        group.MapPost("/test", TestConnection).RequireRateLimiting(RateLimiting.MailConnections);
        group.MapPost("/", Create).RequireRateLimiting(RateLimiting.MailConnections);
        group.MapPut("/{id:guid}", Update).RequireRateLimiting(RateLimiting.MailConnections);
        group.MapPost("/{id:guid}/test", TestStored).RequireRateLimiting(RateLimiting.MailConnections);
        group.MapGet("/{id:guid}/folders", Folders).RequireRateLimiting(RateLimiting.MailConnections);

        return group;
    }

    private static async Task<Ok<List<AccountResponse>>> List(ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var accounts = await db.MailAccounts.Where(a => a.UserId == userId).OrderBy(a => a.Name).ToListAsync(ct);
        return TypedResults.Ok(accounts.Select(AccountResponse.From).ToList());
    }

    private static async Task<Results<Ok<AccountResponse>, NotFound>> Get(Guid id, ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct) =>
        await FindOwnedAsync(db, id, user, ct) is { } account ? TypedResults.Ok(AccountResponse.From(account)) : TypedResults.NotFound();

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Delete(
        Guid id,
        ClaimsPrincipal user,
        MailnixDbContext db,
        RuleService ruleService,
        CancellationToken ct)
    {
        if (await FindOwnedAsync(db, id, user, ct) is not { } account)
        {
            return TypedResults.NotFound();
        }

        // The account's rules go with it, unless rules of other accounts still reference them.
        var rules = await db.Rules.Include(r => r.References).Include(r => r.ListReferences).Where(r => r.AccountId == id).ToListAsync(ct);
        var dependents = await ruleService.DependentsAsync([.. rules.Select(r => r.Id)], ct);
        if (dependents.Count > 0)
        {
            return RuleEndpoints.InUseProblem(
                $"Rules of other accounts reference rules of \"{account.Name}\": {string.Join(", ", dependents.Select(d => d.Dependent.Name).Distinct())}.",
                dependents.Select(d => d.Dependent));
        }

        db.Rules.RemoveRange(rules);
        db.MailAccounts.Remove(account);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<ConnectionTestResponse>> TestConnection(TestConnectionRequest request, IMailboxProbe probe, CancellationToken ct)
    {
        var settings = new ImapConnectionSettings(request.Host.Trim(), request.Port, request.Security, request.Username.Trim(), request.Password);
        try
        {
            var result = await probe.ProbeAsync(settings, includeCounts: false, ct);
            return TypedResults.Ok(new ConnectionTestResponse(true, null, null, ToOverview(result)));
        }
        catch (MailConnectionException ex)
        {
            return TypedResults.Ok(new ConnectionTestResponse(false, ex.Code, ex.Message, null));
        }
    }

    private static async Task<Results<Created<AccountResponse>, ValidationProblem, ProblemHttpResult>> Create(
        CreateAccountRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        IMailboxProbe probe,
        MailSecretProtector secrets,
        TimeProvider time,
        CancellationToken ct)
    {
        var settings = new ImapConnectionSettings(request.Host.Trim(), request.Port, request.Security, request.Username.Trim(), request.Password);
        MailboxProbeResult result;
        try
        {
            result = await probe.ProbeAsync(settings, includeCounts: false, ct);
        }
        catch (MailConnectionException ex)
        {
            return ConnectionProblem(ex);
        }

        var watched = request.WatchedFolders.Distinct(StringComparer.Ordinal).ToList();
        var special = request.SpecialFolders ?? result.SpecialFolders;
        if (ValidateFolders(result, watched, special) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var userId = user.GetUserId();
        var tenantId = await db.Users.Where(u => u.Id == userId).Select(u => u.TenantId).SingleAsync(ct);
        var now = time.GetUtcNow();
        var account = new MailAccount
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            Name = request.Name.Trim(),
            Host = settings.Host,
            Port = settings.Port,
            Security = settings.Security,
            Username = settings.Username,
            EncryptedSecret = secrets.Protect(request.Password),
            WatchedFolders = watched,
            SpecialFolders = ToEntity(special),
            BlastRadiusLimit = request.BlastRadiusLimit,
            PurgeTrashAfterDays = request.PurgeTrashAfterDays,
            CreatedAt = now,
        };
        ApplySuccess(account, result, now);

        db.MailAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/accounts/{account.Id}", AccountResponse.From(account));
    }

    private static async Task<Results<Ok<AccountResponse>, NotFound, ValidationProblem, ProblemHttpResult>> Update(
        Guid id,
        UpdateAccountRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        IMailboxProbe probe,
        MailSecretProtector secrets,
        TimeProvider time,
        CancellationToken ct)
    {
        if (await FindOwnedAsync(db, id, user, ct) is not { } account)
        {
            return TypedResults.NotFound();
        }

        var settings = new ImapConnectionSettings(request.Host.Trim(), request.Port, request.Security, request.Username.Trim(), request.Password ?? string.Empty);
        var newPassword = !string.IsNullOrEmpty(request.Password);
        var connectionChanged = newPassword
            || settings.Host != account.Host
            || settings.Port != account.Port
            || settings.Security != account.Security
            || settings.Username != account.Username;
        var watched = request.WatchedFolders.Distinct(StringComparer.Ordinal).ToList();
        var foldersChanged = !watched.SequenceEqual(account.WatchedFolders) || request.SpecialFolders != AccountResponse.From(account).SpecialFolders;
        var now = time.GetUtcNow();

        // Only changes that could break mail processing are verified against the server.
        if (connectionChanged || foldersChanged)
        {
            if (!newPassword)
            {
                if (TryUnprotect(secrets, account) is not { } storedPassword)
                {
                    return CredentialsUnreadableProblem();
                }

                settings = settings with { Password = storedPassword };
            }

            MailboxProbeResult result;
            try
            {
                result = await probe.ProbeAsync(settings, includeCounts: false, ct);
            }
            catch (MailConnectionException ex)
            {
                return ConnectionProblem(ex);
            }

            if (ValidateFolders(result, watched, request.SpecialFolders) is { Count: > 0 } errors)
            {
                return TypedResults.ValidationProblem(errors);
            }

            account.Host = settings.Host;
            account.Port = settings.Port;
            account.Security = settings.Security;
            account.Username = settings.Username;
            if (newPassword)
            {
                account.EncryptedSecret = secrets.Protect(settings.Password);
            }

            account.WatchedFolders = watched;
            account.SpecialFolders = ToEntity(request.SpecialFolders);
            ApplySuccess(account, result, now);
        }

        account.Name = request.Name.Trim();
        account.Enabled = request.Enabled;
        account.BlastRadiusLimit = request.BlastRadiusLimit;
        account.PurgeTrashAfterDays = request.PurgeTrashAfterDays;
        account.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(AccountResponse.From(account));
    }

    private static async Task<Results<Ok<ConnectionTestResponse>, NotFound, ProblemHttpResult>> TestStored(
        Guid id,
        ClaimsPrincipal user,
        MailnixDbContext db,
        IMailboxProbe probe,
        MailSecretProtector secrets,
        TimeProvider time,
        CancellationToken ct)
    {
        if (await FindOwnedAsync(db, id, user, ct) is not { } account)
        {
            return TypedResults.NotFound();
        }

        if (TryUnprotect(secrets, account) is not { } password)
        {
            return CredentialsUnreadableProblem();
        }

        ConnectionTestResponse response;
        try
        {
            var result = await probe.ProbeAsync(ToSettings(account, password), includeCounts: false, ct);
            ApplySuccess(account, result, time.GetUtcNow());
            response = new ConnectionTestResponse(true, null, null, ToOverview(result));
        }
        catch (MailConnectionException ex)
        {
            ApplyFailure(account, ex, time.GetUtcNow());
            response = new ConnectionTestResponse(false, ex.Code, ex.Message, null);
        }

        if (!await TrySaveStatusAsync(db, ct))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<FolderListResponse>, NotFound, ProblemHttpResult>> Folders(
        Guid id,
        ClaimsPrincipal user,
        MailnixDbContext db,
        IMailboxProbe probe,
        MailSecretProtector secrets,
        TimeProvider time,
        CancellationToken ct,
        bool counts = true)
    {
        if (await FindOwnedAsync(db, id, user, ct) is not { } account)
        {
            return TypedResults.NotFound();
        }

        if (TryUnprotect(secrets, account) is not { } password)
        {
            return CredentialsUnreadableProblem();
        }

        try
        {
            var result = await probe.ProbeAsync(ToSettings(account, password), counts, ct);
            ApplySuccess(account, result, time.GetUtcNow());
            return await TrySaveStatusAsync(db, ct)
                ? TypedResults.Ok(new FolderListResponse(result.Delimiter.ToString(), result.Folders, result.SpecialFolders))
                : TypedResults.NotFound();
        }
        catch (MailConnectionException ex)
        {
            ApplyFailure(account, ex, time.GetUtcNow());
            return await TrySaveStatusAsync(db, ct) ? ConnectionProblem(ex) : TypedResults.NotFound();
        }
    }

    /// <summary>Saves the connection status; false if the account was deleted while the server was being checked.</summary>
    private static async Task<bool> TrySaveStatusAsync(MailnixDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static Task<MailAccount?> FindOwnedAsync(MailnixDbContext db, Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        // Other users' accounts are reported as not found, never as forbidden.
        var userId = user.GetUserId();
        return db.MailAccounts.SingleOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
    }

    private static Dictionary<string, string[]> ValidateFolders(MailboxProbeResult mailbox, IReadOnlyList<string> watched, SpecialFolderMap special)
    {
        var selectable = mailbox.Folders.Where(f => f.CanSelect).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var errors = new Dictionary<string, string[]>();

        var missing = watched.Where(f => !selectable.Contains(f)).ToArray();
        if (missing.Length > 0)
        {
            errors["watchedFolders"] = [.. missing.Select(f => $"Folder '{f}' does not exist on the server.")];
        }

        foreach (var (key, path) in new[] { ("sent", special.Sent), ("drafts", special.Drafts), ("trash", special.Trash), ("junk", special.Junk), ("archive", special.Archive) })
        {
            if (path is not null && !selectable.Contains(path))
            {
                errors[$"specialFolders.{key}"] = [$"Folder '{path}' does not exist on the server."];
            }
        }

        return errors;
    }

    private static void ApplySuccess(MailAccount account, MailboxProbeResult result, DateTimeOffset now)
    {
        account.Status = MailAccountStatus.Ok;
        account.LastErrorCode = null;
        account.LastError = null;
        account.LastCheckedAt = now;
        account.Capabilities = [.. result.Capabilities.All];
        account.FolderDelimiter = result.Delimiter.ToString();
        account.UpdatedAt = now;
    }

    private static void ApplyFailure(MailAccount account, MailConnectionException error, DateTimeOffset now)
    {
        account.Status = MailAccountStatus.Error;
        account.LastErrorCode = error.Code;
        account.LastError = error.Message.Length > 1000 ? error.Message[..1000] : error.Message;
        account.LastCheckedAt = now;
    }

    private static ImapConnectionSettings ToSettings(MailAccount account, string password) =>
        new(account.Host, account.Port, account.Security, account.Username, password);

    private static string? TryUnprotect(MailSecretProtector secrets, MailAccount account)
    {
        try
        {
            return secrets.Unprotect(account.EncryptedSecret);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static MailAccountSpecialFolders ToEntity(SpecialFolderMap map) => new()
    {
        Sent = map.Sent,
        Drafts = map.Drafts,
        Trash = map.Trash,
        Junk = map.Junk,
        Archive = map.Archive,
    };

    private static MailboxOverview ToOverview(MailboxProbeResult result) =>
        new(result.Capabilities, result.Delimiter.ToString(), result.Folders, result.SpecialFolders);

    private static ProblemHttpResult ConnectionProblem(MailConnectionException error) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: error.Message,
            extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code.ToString() });

    // Happens if the data protection keys were lost (e.g. database restored without them).
    private static ProblemHttpResult CredentialsUnreadableProblem() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "The stored password can no longer be decrypted. Please enter it again.",
            extensions: new Dictionary<string, object?> { ["errorCode"] = "CredentialsUnreadable" });
}
