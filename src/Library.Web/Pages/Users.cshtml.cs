using Library.Web.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Library.Web.Pages;

/// <summary>Admins manage accounts: add, reset a password, grant or remove admin, delete.</summary>
public sealed class UsersModel(UserStore users, ILogger<UsersModel> log) : PageModel
{
    public AppUser? Me { get; private set; }
    public IReadOnlyList<UserSummary> Accounts { get; private set; } = [];

    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? Error { get; set; }

    public IActionResult OnGet() => Load() ?? Page();

    public IActionResult OnPostAdd(string? userName, string? password, string? repeatPassword, bool isAdmin, bool mustChangePassword)
    {
        if (Load() is { } denied)
        {
            return denied;
        }

        var problem = UserStore.CheckUserName(userName) ?? UserStore.CheckPassword(password, repeatPassword);
        if (problem is not null)
        {
            return Done(error: problem);
        }

        if (users.Create(userName!, password!, isAdmin, mustChangePassword) is not { } created)
        {
            return Done(error: $"There is already a user called {userName!.Trim()}.");
        }

        log.LogInformation("'{Admin}' added user '{UserName}' (admin: {IsAdmin})", Me!.UserName, created.UserName, created.IsAdmin);
        return Done(created.MustChangePassword
            ? $"Added {created.UserName}. Give them the password; they'll choose their own when they first log in."
            : $"Added {created.UserName}. Give them the password; they can change it under their name in the top bar.");
    }

    public IActionResult OnPostReset(long id, string? password, string? repeatPassword, bool mustChangePassword)
    {
        if (Load() is { } denied)
        {
            return denied;
        }

        if (id == Me!.Id)
        {
            return Done(error: "Change your own password on your Account page.");
        }

        if (UserStore.CheckPassword(password, repeatPassword) is { } problem)
        {
            return Done(error: problem);
        }

        var name = NameOf(id);
        if (name is null || !users.ResetPassword(id, password!, mustChangePassword))
        {
            return Done(error: "That user doesn't exist any more.");
        }

        log.LogInformation("'{Admin}' reset the password of '{UserName}'", Me.UserName, name);
        return Done(mustChangePassword
            ? $"New password set for {name}. They've been logged out everywhere and will choose their own at the next login."
            : $"New password set for {name}. They've been logged out everywhere.");
    }

    public IActionResult OnPostAdmin(long id, bool admin)
    {
        if (Load() is { } denied)
        {
            return denied;
        }

        var name = NameOf(id);
        switch (users.SetAdmin(id, admin))
        {
            case UserChange.LastAdmin:
                return Done(error: "There has to be at least one admin.");
            case UserChange.NotFound:
                return Done(error: "That user doesn't exist any more.");
        }

        log.LogInformation("'{Admin}' {Change} admin rights for '{UserName}'", Me!.UserName, admin ? "granted" : "removed", name);
        if (id == Me.Id && !admin)
        {
            return Redirect("/browse");
        }

        return Done(admin ? $"{name} is now an admin." : $"{name} is no longer an admin.");
    }

    public IActionResult OnPostDelete(long id)
    {
        if (Load() is { } denied)
        {
            return denied;
        }

        if (id == Me!.Id)
        {
            return Done(error: "You can't delete your own account.");
        }

        var name = NameOf(id);
        switch (users.Delete(id))
        {
            case UserChange.LastAdmin:
                return Done(error: "There has to be at least one admin.");
            case UserChange.NotFound:
                return Done(error: "That user doesn't exist any more.");
        }

        log.LogInformation("'{Admin}' deleted user '{UserName}'", Me.UserName, name);
        return Done($"Deleted {name}.");
    }

    /// <summary>Loads the current admin and the list; returns a 403 page for anyone who isn't an admin.</summary>
    private IActionResult? Load()
    {
        Me = Session.UserId(User) is { } id ? users.FindById(id) : null;
        ViewData["Title"] = "Users";
        ViewData["ShellClass"] = "shell--wide";
        if (Me is not { IsAdmin: true })
        {
            Me = null;
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return Page();
        }

        Accounts = users.List();
        return null;
    }

    private string? NameOf(long id) => Accounts.FirstOrDefault(a => a.Id == id)?.UserName;

    private RedirectToPageResult Done(string? message = null, string? error = null)
    {
        Message = message;
        Error = error;
        return RedirectToPage();
    }
}
