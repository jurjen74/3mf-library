using System.Security.Cryptography;
using System.Text;

namespace Library.Web.Auth;

/// <summary>
/// A one-time code for creating the first account when no <c>TRUSTED_NETWORKS</c> are set. Then any direct visitor may
/// open the setup page, so whoever finds a freshly started site first could otherwise claim the admin account. The
/// code is written to the container log at startup (only whoever runs the container can read it) and asked for on the
/// setup page. With trusted networks, setup is limited to those and needs no code.
/// </summary>
public sealed class SetupCode
{
    // No 0/O, 1/I/L: easy to copy from a log by hand.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly string _value = RandomNumberGenerator.GetString(Alphabet, 8);

    /// <summary>As shown in the log: <c>ABCD-EFGH</c>.</summary>
    public string Display => $"{_value[..4]}-{_value[4..]}";

    public static bool IsRequired(LibraryOptions options) => options.TrustedNetworks.Count == 0;

    /// <summary>Case, spaces and the dash don't matter; compared in constant time.</summary>
    public bool Matches(string? entered)
    {
        var normalised = string.Concat((entered ?? "").Where(char.IsLetterOrDigit)).ToUpperInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(normalised), Encoding.ASCII.GetBytes(_value));
    }
}
