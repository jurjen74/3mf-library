namespace Library.Web.Pages.Shared;

/// <summary>A breadcrumb: the folder links above the current page, then the page's own name.</summary>
public sealed record CrumbTrail(IReadOnlyList<Crumb> Links, string Current);
