using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using Ganss.Xss;

namespace Library.Web.Indexing;

/// <summary>
/// Turns a MakerWorld/Bambu description into safe, plain-styled HTML: text structure and links stay; scripts, styles,
/// remote images and MakerWorld's promotional blocks go. Images are covered by the photos embedded in the 3MF.
/// </summary>
public sealed partial class DescriptionSanitizer
{
    private readonly HtmlSanitizer _sanitizer;

    public DescriptionSanitizer()
    {
        _sanitizer = new HtmlSanitizer
        {
            // Unknown wrappers (div, span, figure, font…) disappear but their text stays.
            KeepChildNodes = true,
        };

        _sanitizer.AllowedTags.Clear();
        foreach (var tag in new[]
                 {
                     "p", "br", "strong", "b", "em", "i", "u", "s", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li",
                     "a", "blockquote", "code", "pre", "hr", "table", "thead", "tbody", "tr", "th", "td", "sub", "sup",
                 })
        {
            _sanitizer.AllowedTags.Add(tag);
        }

        _sanitizer.AllowedAttributes.Clear();
        _sanitizer.AllowedAttributes.Add("href");
        _sanitizer.AllowedAttributes.Add("title");
        _sanitizer.AllowedCssProperties.Clear();
        _sanitizer.AllowedAtRules.Clear();
        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.AllowedSchemes.Add("http");
        _sanitizer.AllowedSchemes.Add("https");
        _sanitizer.AllowedSchemes.Add("mailto");

        _sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is IHtmlAnchorElement anchor)
            {
                anchor.SetAttribute("target", "_blank");
                anchor.SetAttribute("rel", "noopener noreferrer nofollow");
            }
        };
    }

    public string? Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var withoutPromotions = PromotionBlocks().Replace(html, "");
        var clean = EmptyParagraphs().Replace(_sanitizer.Sanitize(withoutPromotions), "").Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>The text of already-sanitised HTML, for the search index.</summary>
    public static string? ToPlainText(string? sanitizedHtml) => sanitizedHtml is null
        ? null
        : System.Net.WebUtility.HtmlDecode(Tags().Replace(sanitizedHtml, " "));

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();

    /// <summary>
    /// Elements removed with their content before sanitising: MakerWorld's "commercial licence" and "boost me" adverts,
    /// and anything whose text must never surface (KeepChildNodes would otherwise keep a script's source as text).
    /// </summary>
    [GeneratedRegex(@"<(commercialme|boostme|script|style|noscript|template|iframe|object|svg|math)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PromotionBlocks();

    [GeneratedRegex(@"<p>(\s|&nbsp;|<br>)*</p>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmptyParagraphs();
}
