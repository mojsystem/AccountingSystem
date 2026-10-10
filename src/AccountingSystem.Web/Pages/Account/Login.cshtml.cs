using System.ComponentModel.DataAnnotations;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Account;

public class LoginModel : PageModel
{
    private readonly UserService _users;

    public LoginModel(UserService users)
    {
        _users = users;
    }

    [BindProperty]
    public LoginInput Input { get; set; } = new();

    public string? ReturnUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl, CancellationToken ct)
    {
        if (await _users.CountUsersAsync(ct) == 0)
        {
            return RedirectToPage("/Account/Setup");
        }
        ReturnUrl = returnUrl;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken ct)
    {
        ReturnUrl = returnUrl;
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await _users.SignInAsync(Input.Username, Input.Password, ct);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "نام کاربری یا رمز عبور اشتباه است.");
            return Page();
        }

        await HttpContext.SignInUserAsync(user, persistent: false);
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }
        return RedirectToPage("/Index");
    }
}

public sealed class LoginInput
{
    [Required(ErrorMessage = "نام کاربری را وارد کنید.")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "رمز عبور را وارد کنید.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;
}
