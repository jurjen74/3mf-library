using System.Reflection;

namespace Library.Web.Updates;

/// <summary>
/// The version of this build, set by the image build from <c>git describe</c>: <c>1.0.1</c> for a release,
/// <c>1.0.1-2-gf43c89b</c> for a build two commits after it, <c>dev</c> for a local build.
/// </summary>
public sealed record AppVersion(string Text)
{
    public static AppVersion Current { get; } = new(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev");

    /// <summary>The release this build is (or builds on), as major.minor.patch; null for a local build.</summary>
    public Version? Release => ParseRelease(Text);

    /// <summary>Exactly a release, not a later build from the main branch.</summary>
    public bool IsRelease => Release is not null && !Text.TrimStart('v').Contains('-', StringComparison.Ordinal);

    /// <summary>"1.0.1", "v1.0.1", "1.0.1-2-gf43c89b" → 1.0.1; anything else → null.</summary>
    public static Version? ParseRelease(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return core.Split('.').Length == 3 && Version.TryParse(core, out var version) ? version : null;
    }

    public override string ToString() => Text;
}
