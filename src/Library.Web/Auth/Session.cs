using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Library.Web.Auth;

/// <summary>The login cookie: who you are, plus the security stamp it was issued with.</summary>
public static class Session
{
    private const string StampClaim = "library:stamp";
    private const string UserKey = "library.user";

    // While a password change is required, only these paths are reachable (plus nothing else).
    private static readonly string[] OpenWhileChangeRequired = ["/account", "/logout", "/health", "/css/", "/js/", "/lib/", "/icons/", "/fonts/", "/backgrounds/", "/favicon.svg", "/manifest.webmanifest"];

    public static Task SignInAsync(HttpContext context, AppUser user, bool persistent)
    {
        // Admin rights aren't in the cookie: pages check the database, so changes apply at once.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.UserName),
            new(StampClaim, user.SecurityStamp),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = persistent, AllowRefresh = true });
    }

    public static long? UserId(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>Ends a session whose user was removed or whose password changed since the cookie was issued.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var users = context.HttpContext.RequestServices.GetRequiredService<UserStore>();
        var user = context.Principal is { } principal && UserId(principal) is { } id ? users.FindById(id) : null;
        if (user is null || user.SecurityStamp != context.Principal!.FindFirstValue(StampClaim))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        context.HttpContext.Items[UserKey] = user;
    }

    /// <summary>The logged-in account as loaded for this request; null for home-network and anonymous visitors.</summary>
    public static AppUser? CurrentUser(HttpContext context) => context.Items[UserKey] as AppUser;

    /// <summary>
    /// Whether this visitor may take files away: downloads, folder ZIPs, slicer links and the 3D viewer's meshes.
    /// The home network and admins always can; a browse-only account can't. Read from the database per request.
    /// </summary>
    public static bool CanDownload(HttpContext context) =>
        TrustedNetwork.IsHomeNetworkUser(context.User) || CurrentUser(context) is { CanDownload: true };

    /// <summary>
    /// Middleware: someone with a starting or reset password is sent to the Account page until they've chosen their
    /// own. Pages, previews, downloads and meshes all wait.
    /// </summary>
    public static async Task RequirePasswordChange(HttpContext context, Func<Task> next)
    {
        var path = context.Request.Path.Value ?? "/";
        if (CurrentUser(context) is { MustChangePassword: true } &&
            !OpenWhileChangeRequired.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                                              (p.EndsWith('/') && path.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
        {
            context.Response.Redirect("/account");
            return;
        }

        await next();
    }
}
