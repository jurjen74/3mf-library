using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace Library.Web;

/// <summary>
/// "Open in Bambu Studio": the slicer downloads the file itself, without the browser's login cookie. So the button asks
/// for a link signed with the app's data-protection keys, valid for one file and a few minutes, which <c>/dl/…</c>
/// serves without a login.
/// </summary>
public sealed partial class SlicerLinks(IDataProtectionProvider protection)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public const string Purpose = "library.slicer-link.v1";

    private readonly ITimeLimitedDataProtector _protector =
        protection.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    /// <summary>Absolute download URL for <paramref name="path"/>, valid for <see cref="Lifetime"/>.</summary>
    public string Create(HttpRequest request, string path, string fileName) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}/dl/{_protector.Protect(path, Lifetime)}/{SafeName(fileName)}";

    /// <summary>The library path a token stands for, or null when it's forged, damaged or expired.</summary>
    public string? Resolve(string token)
    {
        try
        {
            return _protector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// The last URL segment, which the slicer uses as the project name: plain characters only, because it percent-decodes
    /// the link and filters "../"-like sequences.
    /// </summary>
    public static string SafeName(string fileName) => UnsafeCharacters().Replace(fileName, "_");

    [GeneratedRegex(@"[^A-Za-z0-9._-]|\.(?=[\\/])", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeCharacters();
}
