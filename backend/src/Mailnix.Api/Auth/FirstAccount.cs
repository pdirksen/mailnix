using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Mailnix.Api.Auth;

/// <summary>
/// The first account administers the installation. So that a stranger who reaches a fresh server first cannot take
/// it, signing up needs a setup code until that account exists: <see cref="AuthOptions.SetupCode"/>, or a random one
/// written to the log at startup. Sign-ups are decided one at a time until then, so two at once cannot both get it.
/// </summary>
internal sealed class FirstAccount(IOptions<AuthOptions> options)
{
    // Without 0/O and 1/I/L, so it can be read off a log and typed.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly string _code = Normalize(options.Value.SetupCode) is { Length: > 0 } configured ? configured : Generate();

    /// <summary>Held while a sign-up decides whether it is the first account.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);

    public bool CodeRequired => options.Value.RequireSetupCode;

    /// <summary>As shown in the log: XXXX-XXXX-XXXX.</summary>
    public string Code => string.Join('-', _code.Chunk(4).Select(c => new string(c)));

    public bool Matches(string? code) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Normalize(code)), Encoding.UTF8.GetBytes(_code));

    private static string Normalize(string? code) =>
        new([.. (code ?? string.Empty).ToUpperInvariant().Where(c => c is not ('-' or ' '))]);

    private static string Generate() => new([.. RandomNumberGenerator.GetItems<char>(Alphabet, 12)]);
}
