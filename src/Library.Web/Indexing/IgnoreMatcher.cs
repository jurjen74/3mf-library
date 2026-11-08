using System.Text.RegularExpressions;

namespace Library.Web.Indexing;

/// <summary>Skips files and folders by name: dotfiles always, plus simple <c>*</c>/<c>?</c> globs from configuration.</summary>
public sealed class IgnoreMatcher
{
    private readonly Regex[] _patterns;

    public IgnoreMatcher(IEnumerable<string> globs) => _patterns = globs
        .Where(g => g.Length > 0)
        .Select(g => new Regex(
            "^" + Regex.Escape(g).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .ToArray();

    public bool IsIgnored(string name) => name.StartsWith('.') || _patterns.Any(p => p.IsMatch(name));
}
