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
    private readonly PermissionService _permissions;

    public IndexModel(UserService users, BranchService branches, PermissionService permissions)
    {
        _users = users;
        _branches = branches;
        _permissions = permissions;
    }

    [BindProperty]
    public NewUserForm Input { get; set; } = new();

    public IReadOnlyList<UserInfo> UserList { get; private set; } = Array.Empty<UserInfo>();

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = Array.Empty<BranchInfo>();

    /// <summary>دسترسی‌های اضافی کاربران صندوق، با کلید شناسه‌ی کاربر.</summary>
    public IReadOnlyDictionary<int, IReadOnlySet<Permission>> Assignments { get; private set; } =
        new Dictionary<int, IReadOnlySet<Permission>>();

    /// <summary>همه‌ی دسترسی‌های قابل اعطا، به ترتیب نمایش.</summary>
    public IReadOnlyList<Permission> AllPermissions { get; } = Enum.GetValues<Permission>();

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

    /// <summary>
    /// تنظیم دسترسی‌های ویرایش و ابطال یک کاربر صندوق (فقط مدیر). دسترسی‌های تیک‌خورده جایگزین قبلی‌ها می‌شوند.
    /// </summary>
    public async Task<IActionResult> OnPostPermissionsAsync(int userId, List<string>? permissions, CancellationToken ct)
    {
        try
        {
            var selected = new List<Permission>();
            foreach (var code in permissions ?? new List<string>())
            {
                if (!PermissionCodes.TryParse(code, out var permission))
                {
                    throw new BusinessRuleException("دسترسی انتخابی نامعتبر است.");
                }
                selected.Add(permission);
            }
            await _permissions.SetPermissionsAsync(User.ToCurrentUser(), userId, selected, DateTime.Now, ct);
            TempData["Success"] = "دسترسی‌های کاربر به‌روز شد و همان لحظه اعمال می‌شود.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage();
    }

    public bool HasPermission(int userId, Permission permission) =>
        Assignments.TryGetValue(userId, out var set) && set.Contains(permission);

    private async Task LoadAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        UserList = await _users.GetUsersAsync(user, ct);
        Branches = await _branches.GetBranchesAsync(ct);
        Assignments = await _permissions.GetAssignmentsAsync(user, ct);
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
