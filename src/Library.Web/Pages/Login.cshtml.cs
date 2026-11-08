using Library.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Library.Web.Pages;

[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class LoginModel(UserStore users, LibraryOptions options, ILogger<LoginModel> log) : PageModel
{
    [BindProperty]
    public string? UserName { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    [BindProperty]
    public bool Remember { get; set; } = true;

    public string? Error { get; private set; }

    /// <summary>True on a fresh install reached through the proxy: there's nobody to log in as yet.</summary>
    public bool NoAccounts { get; private set; }

    public IActionResult OnGet(string? returnUrl)
    {
        if (!users.HasUsers())
        {
            if (TrustedNetwork.MayRunSetup(HttpContext, options))
            {
                return Redirect("/setup");
            }

            NoAccounts = true;
        }
        else if (User.Identity?.IsAuthenticated == true && !TrustedNetwork.IsHomeNetworkUser(User))
        {
            return LocalRedirect(SafeReturnUrl(returnUrl));
        }

        Prepare();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        Prepare();
        if (users.Verify(UserName, Password) is not { } user)
        {
            log.LogWarning("Failed login for '{UserName}' from {Address}", UserName, HttpContext.Connection.RemoteIpAddress);
            Error = "That user name and password don't match.";
            Password = null;
            NoAccounts = !users.HasUsers();
            return Page();
        }

        await Session.SignInAsync(HttpContext, user, Remember);
        log.LogInformation("Login for '{UserName}' from {Address}", user.UserName, HttpContext.Connection.RemoteIpAddress);
        return LocalRedirect(SafeReturnUrl(returnUrl));
    }

    private void Prepare()
    {
        ViewData["Title"] = "Log in";
        ViewData["ShellClass"] = "shell--wide";
        ViewData["Anonymous"] = true;
    }

    private string SafeReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } && Url.IsLocalUrl(returnUrl) ? returnUrl : "/browse";
}
