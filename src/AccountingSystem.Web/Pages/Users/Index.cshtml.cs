using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Users;

public class IndexModel : PageModel
{
    private readonly UserService _users;
    private readonly BranchService _branches;

    public IndexModel(UserService users, BranchService branches)
    {
        _users = users;
        _branches = branches;
    }

    [BindProperty]
    public NewUserForm Input { get; set; } = new();

    public IReadOnlyList<UserInfo> UserList { get; private set; } = Array.Empty<UserInfo>();

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = Array.Empty<BranchInfo>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
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
                Input.BranchId,
                DateTime.Now,
                ct);
            TempData["Success"] = "کاربر جدید ثبت شد.";
            return RedirectToPage();
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        UserList = await _users.GetUsersAsync(user, ct);
        Branches = await _branches.GetBranchesAsync(ct);
    }
}

public sealed class NewUserForm
{
    public string? Username { get; set; }

    public string? FullName { get; set; }

    public string? Password { get; set; }

    public string Role { get; set; } = "Cashier";

    /// <summary>شعبه‌ی کاربر صندوق. برای مدیر نادیده گرفته می‌شود.</summary>
    public int? BranchId { get; set; }
}
