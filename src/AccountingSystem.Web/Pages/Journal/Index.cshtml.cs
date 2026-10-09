using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Journal;

public class IndexModel : PageModel
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly ReportService _reports;
    private readonly BranchService _branches;

    public IndexModel(ReportService reports, BranchService branches)
    {
        _reports = reports;
        _branches = branches;
    }

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? BranchFilter { get; set; }

    public IReadOnlyList<JournalEntryInfo> Entries { get; private set; } = Array.Empty<JournalEntryInfo>();

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = Array.Empty<BranchInfo>();

    public bool IsAdmin { get; private set; }

    public string? RangeError { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        IsAdmin = user.Role == UserRole.Admin;
        Branches = await _branches.GetBranchesAsync(ct);
        var (from, to) = ResolveRange();
        Entries = await _reports.GetJournalAsync(user, user.ScopeFor(BranchFilter), from, to, ct);
    }

    public async Task<IActionResult> OnGetExcelAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        var (from, to) = ResolveRange();
        var entries = await _reports.GetJournalAsync(user, user.ScopeFor(BranchFilter), from, to, ct);
        var bytes = WorkbookBuilder.Journal(entries);
        return File(bytes, ExcelContentType, $"journal-{from:yyyyMMdd}-{to.AddDays(-1):yyyyMMdd}.xlsx");
    }

    /// <summary>بازه‌ی شمسی ورودی را به بازه‌ی میلادی تبدیل می‌کند؛ خالی بودن تاریخ یعنی امروز.</summary>
    private (DateTime From, DateTime To) ResolveRange()
    {
        var today = DateTime.Now;
        if (!PersianDate.TryParseRange(From, To, today, out var fromInclusive, out var toExclusive, out var error))
        {
            RangeError = error;
            fromInclusive = today.Date;
            toExclusive = today.Date.AddDays(1);
        }
        From = PersianDate.FormatDate(fromInclusive);
        To = PersianDate.FormatDate(toExclusive.AddDays(-1));
        return (fromInclusive, toExclusive);
    }
}
