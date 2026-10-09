using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Roles;

public class IndexModel : PageModel
{
    private readonly BranchService _branches;
    private readonly PermissionService _permissions;

    public IndexModel(BranchService branches, PermissionService permissions)
    {
        _branches = branches;
        _permissions = permissions;
    }

    [BindProperty(SupportsGet = true)]
    public int? BranchId { get; set; }

    [BindProperty]
    public NewRoleForm NewRole { get; set; } = new();

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = Array.Empty<BranchInfo>();

    public IReadOnlyList<AccessRoleInfo> Roles { get; private set; } = Array.Empty<AccessRoleInfo>();

    /// <summary>همه‌ی وظیفه‌های قابل اعطا، به ترتیب نمایش.</summary>
    public IReadOnlyList<Permission> AllPermissions { get; } = Enum.GetValues<Permission>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        var branchId = NewRole.BranchId;
        try
        {
            var permissions = ParsePermissions(NewRole.Permissions);
            await _permissions.CreateRoleAsync(User.ToCurrentUser(), branchId, NewRole.Name ?? string.Empty, permissions, DateTime.Now, ct);
            TempData["Success"] = "نقش جدید ثبت شد.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(new { branchId });
    }

    public async Task<IActionResult> OnPostUpdateAsync(int roleId, int branchId, List<string>? permissions, CancellationToken ct)
    {
        try
        {
            await _permissions.SetRolePermissionsAsync(User.ToCurrentUser(), roleId, ParsePermissions(permissions), DateTime.Now, ct);
            TempData["Success"] = "وظیفه‌های نقش به‌روز شد و برای همه‌ی کاربرانی که این نقش را دارند همان لحظه اعمال می‌شود.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(new { branchId });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int roleId, int branchId, CancellationToken ct)
    {
        try
        {
            await _permissions.DeleteRoleAsync(User.ToCurrentUser(), roleId, DateTime.Now, ct);
            TempData["Success"] = "نقش حذف شد.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(new { branchId });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        Branches = await _branches.GetBranchesAsync(ct);
        var selected = BranchId ?? Branches.FirstOrDefault()?.Id;
        BranchId = selected;
        if (selected is { } branch)
        {
            Roles = await _permissions.GetRolesAsync(user, branch, ct);
        }
    }

    private static List<Permission> ParsePermissions(List<string>? codes)
    {
        var result = new List<Permission>();
        foreach (var code in codes ?? new List<string>())
        {
            if (!PermissionCodes.TryParse(code, out var permission))
            {
                throw new BusinessRuleException("وظیفه‌ی انتخابی نامعتبر است.");
            }
            result.Add(permission);
        }
        return result;
    }
}

public sealed class NewRoleForm
{
    public int BranchId { get; set; }

    public string? Name { get; set; }

    public List<string>? Permissions { get; set; }
}
