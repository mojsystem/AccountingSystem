using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Users;

public class IndexModel : PageModel
{
    private readonly UserService _users;

    public IndexModel(UserService users)
    {
        _users = users;
    }

    [BindProperty]
    public NewUserForm Input { get; set; } = new();

    public IReadOnlyList<UserInfo> UserList { get; private set; } = Array.Empty<UserInfo>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        UserList = await _users.GetUsersAsync(User.ToCurrentUser(), ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            var role = Enum.TryParse<UserRole>(Input.Role, out var parsed) ? parsed : UserRole.Cashier;
            await _users.CreateUserAsync(
                User.ToCurrentUser(),
                Input.Username ?? string.Empty,
                Input.FullName ?? string.Empty,
                Input.Password ?? string.Empty,
                role,
                DateTime.Now,
                ct);
            TempData["Success"] = "کاربر جدید ثبت شد.";
            return RedirectToPage();
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            UserList = await _users.GetUsersAsync(User.ToCurrentUser(), ct);
            return Page();
        }
    }
}

public sealed class NewUserForm
{
    public string? Username { get; set; }

    public string? FullName { get; set; }

    public string? Password { get; set; }

    public string Role { get; set; } = "Cashier";
}
