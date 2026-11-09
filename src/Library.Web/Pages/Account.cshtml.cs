using Library.Web.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Library.Web.Pages;

/// <summary>
/// Your account: change the password (which also logs out your other devices). With a starting or reset password
/// this is the only page you can reach until you've chosen your own.
/// </summary>
[EnableRateLimiting("account")]
public sealed class AccountModel(UserStore users, ILogger<AccountModel> log) : PageModel
{
    [BindProperty]
    public string? CurrentPassword { get; set; }

    [BindProperty]
    public string? NewPassword { get; set; }

    [BindProperty]
    public string? RepeatPassword { get; set; }

    public AppUser? Account { get; private set; }

    public bool HasAccounts { get; private set; }

    public string? Error { get; private set; }

    public bool Changed { get; private set; }

    /// <summary>The account still has a password someone else set: choosing a new one is required.</summary>
    public bool Required => Account?.MustChangePassword == true;

    public void OnGet(bool changed = false)
    {
        Prepare();
        Changed = changed;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Prepare();
        if (Account is null)
        {
            return Page();
        }

        Error = UserStore.CheckPassword(NewPassword, RepeatPassword)
                ?? (NewPassword == CurrentPassword ? "Choose a password that's different from the one you have now." : null);
        var wasRequired = Required;
        var updated = Error is null ? users.ChangePassword(Account.Id, CurrentPassword ?? "", NewPassword!) : null;
        if (updated is null)
        {
            Error ??= "Your current password isn't right.";
            CurrentPassword = NewPassword = RepeatPassword = null;
            return Page();
        }

        log.LogInformation("Password changed for '{UserName}'", updated.UserName);
        await Session.SignInAsync(HttpContext, updated, persistent: true);
        return Redirect(wasRequired ? "/browse" : "/account?changed=true");
    }

    private void Prepare()
    {
        Account = Session.UserId(User) is { } id ? users.FindById(id) : null;
        HasAccounts = Account is not null || users.HasUsers();
        ViewData["Title"] = Required ? "Choose your password" : "Account";
        ViewData["ShellClass"] = "shell--narrow";

        // Nothing else is reachable yet, so no search, menu or footer.
        ViewData["Anonymous"] = Required ? true : null;
    }
}
