using System.ComponentModel.DataAnnotations;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Account;

public class SetupModel : PageModel
{
    private readonly UserService _users;

    public SetupModel(UserService users)
    {
        _users = users;
    }

    [BindProperty]
    public SetupInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await _users.CountUsersAsync(ct) > 0)
        {
            return RedirectToPage("/Account/Login");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (await _users.CountUsersAsync(ct) > 0)
        {
            return RedirectToPage("/Account/Login");
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var admin = await _users.CreateFirstAdminAsync(Input.Username, Input.FullName, Input.Password, DateTime.Now, ct);
            await HttpContext.SignInUserAsync(admin, persistent: false);
            return RedirectToPage("/Index");
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }
}

public sealed class SetupInput
{
    [Required(ErrorMessage = "نام و نام خانوادگی را وارد کنید.")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "نام کاربری را وارد کنید.")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "رمز عبور را وارد کنید.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;
}
