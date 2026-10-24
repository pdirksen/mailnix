using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Mailnix.Infrastructure.OAuth;

/// <summary>
/// Connection settings for a stored account: the decrypted password, or for OAuth accounts a current access token
/// (refreshed with the stored refresh token and cached until shortly before it expires). Renewed refresh tokens
/// are saved back encrypted.
/// </summary>
public sealed class AccountCredentials(
    MailSecretProtector secrets,
    OAuthClient oauth,
    IMemoryCache cache,
    IServiceScopeFactory scopes,
    TimeProvider time)
{
    /// <summary>Access tokens are renewed this long before they expire, so a connection never starts with a dying one.</summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshing = new();

    /// <exception cref="CryptographicException">The stored secret can no longer be decrypted.</exception>
    /// <exception cref="MailConnectionException">No access token could be obtained (<see cref="ConnectionErrorCode.ReconnectRequired"/> when the grant is gone).</exception>
    public async Task<ImapConnectionSettings> SettingsAsync(MailAccount account, CancellationToken ct)
    {
        var secret = secrets.Unprotect(account.EncryptedSecret);
        if (account.Provider == MailProvider.Imap)
        {
            return new ImapConnectionSettings(account.Host, account.Port, account.Security, account.Username, secret);
        }

        var accessToken = await AccessTokenAsync(account, secret, ct);
        return new ImapConnectionSettings(account.Host, account.Port, account.Security, account.Username, accessToken, ImapAuthentication.OAuth2);
    }

    /// <summary>Remembers the access token from a fresh sign-in, so the first connections need no refresh.</summary>
    public void Remember(Guid accountId, string refreshToken, OAuthTokens tokens)
    {
        // Short-lived tokens are used once and not kept.
        if (tokens.ExpiresAt - ExpiryMargin > time.GetUtcNow())
        {
            cache.Set(Key(accountId, refreshToken), tokens.AccessToken, tokens.ExpiresAt - ExpiryMargin);
        }
    }

    private async Task<string> AccessTokenAsync(MailAccount account, string refreshToken, CancellationToken ct)
    {
        if (cache.TryGetValue(Key(account.Id, refreshToken), out string? cached) && cached is not null)
        {
            return cached;
        }

        // One refresh per account at a time (watcher, preview and connection tests may ask together).
        var gate = _refreshing.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(Key(account.Id, refreshToken), out cached) && cached is not null)
            {
                return cached;
            }

            OAuthTokens tokens;
            try
            {
                tokens = await oauth.RefreshAsync(account.Provider, refreshToken, ct);
            }
            catch (OAuthException ex) when (ex.ReauthorizationRequired)
            {
                throw new MailConnectionException(
                    ConnectionErrorCode.ReconnectRequired,
                    $"The access to this {account.Provider} mailbox was revoked or has expired. Reconnect the account.",
                    ex);
            }
            catch (Exception ex) when (ex is OAuthException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new MailConnectionException(ConnectionErrorCode.ConnectionFailed, $"Could not get an access token from {account.Provider}: {ex.Message}", ex);
            }

            if (tokens.RefreshToken is { Length: > 0 } renewed && renewed != refreshToken)
            {
                await SaveRefreshTokenAsync(account.Id, renewed, ct);
                Remember(account.Id, renewed, tokens);
            }

            Remember(account.Id, refreshToken, tokens);
            return tokens.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SaveRefreshTokenAsync(Guid accountId, string refreshToken, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
        var encrypted = secrets.Protect(refreshToken);

        // Not through UpdatedAt: that would count as a settings change and restart the account's watcher.
        await db.MailAccounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(u => u.SetProperty(a => a.EncryptedSecret, encrypted), ct);
    }

    /// <summary>Per account and grant: reconnecting (a new refresh token) never reuses an old access token.</summary>
    private static string Key(Guid accountId, string refreshToken) =>
        $"oauth:{accountId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)), 0, 8)}";
}
