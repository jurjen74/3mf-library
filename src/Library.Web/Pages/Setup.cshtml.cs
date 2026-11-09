using Library.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Library.Web.Pages;

/// <summary>
/// First run: create the first account (an admin). Only while no account exists, never through a proxy; from the home
/// network when <c>TRUSTED_NETWORKS</c> is set, otherwise with the <see cref="Auth.SetupCode"/> from the container log.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class SetupModel(UserStore users, LibraryOptions options, SetupCode setupCode, ILogger<SetupModel> log) : PageModel
{
    [BindProperty]
    public string? Code { get; set; }

    /// <summary>No trusted networks: the setup code from the container log is needed.</summary>
    public bool CodeRequired { get; private set; }

    [BindProperty]
    public string? UserName { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    [BindProperty]
    public string? RepeatPassword { get; set; }

    public string? Error { get; private set; }

    public bool Allowed { get; private set; }

    public IActionResult OnGet()
    {
        if (users.HasUsers())
        {
            return Redirect("/login");
        }

        Prepare();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (users.HasUsers())
        {
            return Redirect("/login");
        }

        Prepare();
        if (!Allowed)
        {
            return Page();
        }

        if (CodeRequired && !setupCode.Matches(Code))
        {
            log.LogWarning("Setup with a wrong setup code from {Address}", HttpContext.Connection.RemoteIpAddress);
            Error = "That setup code isn't right. It's in the container log (docker logs 3mf-library).";
            Password = RepeatPassword = null;
            return Page();
        }

        Error = UserStore.CheckUserName(UserName) ?? UserStore.CheckPassword(Password, RepeatPassword);
        if (Error is not null)
        {
            Password = RepeatPassword = null;
            return Page();
        }

        var user = users.CreateFirstAdmin(UserName!, Password!);
        if (user is null)
        {
            // Someone else finished the setup a moment earlier.
            return Redirect("/login");
        }

        log.LogInformation("First account '{UserName}' created from {Address}", user.UserName, HttpContext.Connection.RemoteIpAddress);
        await Session.SignInAsync(HttpContext, user, persistent: true);
        return Redirect("/browse");
    }

    private void Prepare()
    {
        Allowed = TrustedNetwork.MayRunSetup(HttpContext, options);
        CodeRequired = SetupCode.IsRequired(options);
        ViewData["Title"] = "Set up";
        ViewData["ShellClass"] = "shell--wide";
        ViewData["Anonymous"] = true;
    }
}
