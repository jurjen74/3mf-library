using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Library.Web.Auth;

/// <summary>The login cookie: who you are, plus the security stamp it was issued with.</summary>
public static class Session
{
    private const string StampClaim = "library:stamp";
    private const string AdminRole = "admin";

    public static Task SignInAsync(HttpContext context, AppUser user, bool persistent)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.UserName),
            new(StampClaim, user.SecurityStamp),
        };
        if (user.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, AdminRole));
        }

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
        }
    }
}
