using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages;

public class IndexModel : PageModel
{
    private readonly ReportService _reports;
    private readonly PermissionService _permissions;

    public IndexModel(ReportService reports, PermissionService permissions)
    {
        _reports = reports;
        _permissions = permissions;
    }

    [BindProperty(SupportsGet = true)]
    public int? BranchFilter { get; set; }

    public DashboardInfo? Dashboard { get; private set; }

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = Array.Empty<BranchInfo>();

    public bool IsAdmin { get; private set; }

    public string? Notice { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        IsAdmin = user.Role == UserRole.Admin;
        var access = await _permissions.GetAccessAsync(user, ct);
        Branches = await _permissions.GetBranchesAsync(user, null, ct);
        var (branch, notice) = WebExtensions.ReadableBranchFilter(access, BranchFilter);
        Notice = notice;
        Dashboard = await _reports.GetDashboardAsync(user, branch, DateTime.Now, ct);
    }
}
