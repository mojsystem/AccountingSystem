using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages;

public class IndexModel : PageModel
{
    private readonly ReportService _reports;

    public IndexModel(ReportService reports)
    {
        _reports = reports;
    }

    public DashboardInfo? Dashboard { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Dashboard = await _reports.GetDashboardAsync(DateTime.Now, ct);
    }
}
