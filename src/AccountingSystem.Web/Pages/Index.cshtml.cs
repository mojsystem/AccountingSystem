using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages;

public class IndexModel : PageModel
{
    private readonly ReportService _reports;
    private readonly BranchService _branches;

    public IndexModel(ReportService reports, BranchService branches)
    {
        _reports = reports;
        _branches = branches;
    }

    [BindProperty(SupportsGet = true)]
    public int? BranchFilter { get; set; }

    public DashboardInfo? Dashboard { get; private set; }

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = Array.Empty<BranchInfo>();

    public bool IsAdmin { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        IsAdmin = user.Role == UserRole.Admin;
        Branches = await _branches.GetBranchesAsync(ct);
        Dashboard = await _reports.GetDashboardAsync(user, user.ScopeFor(BranchFilter), DateTime.Now, ct);
    }
}
