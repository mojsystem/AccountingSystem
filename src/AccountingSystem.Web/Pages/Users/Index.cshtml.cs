using System.Globalization;
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

    /// <summary>دسترسی هر کاربر در همه‌ی شعبه‌ها، با کلید شناسه‌ی کاربر.</summary>
    public IReadOnlyDictionary<int, UserAccess> Access { get; private set; } = new Dictionary<int, UserAccess>();

    /// <summary>نقش‌های تعریف‌شده‌ی هر شعبه، با کلید شناسه‌ی شعبه.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<AccessRoleInfo>> RolesByBranch { get; private set; } =
        new Dictionary<int, IReadOnlyList<AccessRoleInfo>>();

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
            TempData["Success"] = "کاربر جدید ثبت شد. شعبه‌ها و نقش‌های او را در همین صفحه تنظیم کنید.";
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
    /// ذخیره‌ی عضویت‌های یک کاربر: برای هر شعبه یک نقش یا هیچ‌کدام، و شعبه‌ی اصلی. تغییرات همان لحظه اعمال می‌شوند.
    /// </summary>
    public async Task<IActionResult> OnPostMembershipsAsync(int userId, int? defaultBranchId, CancellationToken ct)
    {
        try
        {
            var actor = User.ToCurrentUser();
            var branches = await _branches.GetBranchesAsync(ct);
            var roleByBranch = new Dictionary<int, int?>();
            foreach (var branch in branches)
            {
                var raw = Request.Form["role_" + branch.Id.ToString(CultureInfo.InvariantCulture)].ToString();
                var parsedOk = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roleId);
                roleByBranch[branch.Id] = parsedOk && roleId > 0 ? roleId : (int?)null;
            }

            var current = await _permissions.GetAllAccessAsync(actor, ct);
            int? fallbackDefault = current.TryGetValue(userId, out var currentAccess) ? currentAccess.DefaultBranchId : null;
            await _permissions.ApplyMembershipsAsync(actor, userId, roleByBranch, defaultBranchId ?? fallbackDefault, DateTime.Now, ct);
            TempData["Success"] = "شعبه‌ها و نقش‌های کاربر به‌روز شد و همان لحظه اعمال می‌شود.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        UserList = await _users.GetUsersAsync(user, ct);
        Branches = await _branches.GetBranchesAsync(ct);
        Access = await _permissions.GetAllAccessAsync(user, ct);
        var roles = new Dictionary<int, IReadOnlyList<AccessRoleInfo>>();
        foreach (var branch in Branches)
        {
            roles[branch.Id] = await _permissions.GetRolesAsync(user, branch.Id, ct);
        }
        RolesByBranch = roles;
    }
}

public sealed class NewUserForm
{
    public string? Username { get; set; }

    public string? FullName { get; set; }

    public string? Password { get; set; }

    public string Role { get; set; } = "Cashier";

    /// <summary>شعبه‌ی اصلی کاربر شعبه (کاربر با نقش «کاربر صندوق» در این شعبه ساخته می‌شود). برای مدیر نادیده گرفته می‌شود.</summary>
    public int? BranchId { get; set; }
}
