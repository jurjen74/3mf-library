using Library.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Library.Web.Pages;

/// <summary>First run: create the first account (an admin). Only while no account exists, only from the home network.</summary>
[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class SetupModel(UserStore users, LibraryOptions options, ILogger<SetupModel> log) : PageModel
{
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
        ViewData["Title"] = "Set up";
        ViewData["ShellClass"] = "shell--wide";
        ViewData["Anonymous"] = true;
    }
}
