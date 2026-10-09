using AccountingSystem.Core.Accounting;
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
    private readonly ManualJournalService _manual;

    public IndexModel(ReportService reports, BranchService branches, ManualJournalService manual)
    {
        _reports = reports;
        _branches = branches;
        _manual = manual;
    }

    [BindProperty]
    public ManualForm Manual { get; set; } = new();

    public IReadOnlyList<AccountInfo> Accounts { get; private set; } = Array.Empty<AccountInfo>();

    /// <summary>ثبت سند حسابداری دستی (فقط مدیر). سطرهای خالی نادیده گرفته می‌شوند.</summary>
    public async Task<IActionResult> OnPostManualAsync(CancellationToken ct)
    {
        try
        {
            var lines = ParseLines(Manual.Lines);
            var occurredOn = ParseDate(Manual.OccurredOn);
            var entryId = await _manual.CreateAsync(User.ToCurrentUser(), Manual.BranchId, Manual.Description ?? string.Empty, occurredOn, lines, DateTime.Now, ct);
            TempData["Success"] = $"سند دستی شماره {entryId} ثبت شد.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(new { From, To, BranchFilter });
    }

    /// <summary>ابطال سند دستی (فقط مدیر). سند معکوس ثبت می‌شود و سند اصلی علامت باطل می‌خورد.</summary>
    public async Task<IActionResult> OnPostVoidManualAsync(long entryId, string? voidReason, CancellationToken ct)
    {
        try
        {
            await _manual.VoidAsync(User.ToCurrentUser(), entryId, voidReason ?? string.Empty, DateTime.Now, ct);
            TempData["Success"] = $"سند دستی شماره {entryId} باطل شد و سند ابطال ثبت گردید.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(new { From, To, BranchFilter });
    }

    internal static IReadOnlyList<JournalLineDraft> ParseLines(IEnumerable<ManualLineForm> rows)
    {
        var lines = new List<JournalLineDraft>();
        foreach (var row in rows)
        {
            var code = row.AccountCode?.Trim();
            var hasDebit = !string.IsNullOrWhiteSpace(row.Debit);
            var hasCredit = !string.IsNullOrWhiteSpace(row.Credit);
            if (string.IsNullOrEmpty(code) && !hasDebit && !hasCredit)
            {
                continue;
            }
            if (string.IsNullOrEmpty(code))
            {
                throw new BusinessRuleException("برای هر سطر مبلغ‌دار، حساب را انتخاب کنید.");
            }
            if ((hasDebit && !InputParser.TryParseDecimal(row.Debit, out _)) || (hasCredit && !InputParser.TryParseDecimal(row.Credit, out _)))
            {
                throw new BusinessRuleException("مبلغ سطرهای سند را به‌درستی وارد کنید (عدد ریال).");
            }
            InputParser.TryParseDecimal(row.Debit, out var debit);
            InputParser.TryParseDecimal(row.Credit, out var credit);
            lines.Add(new JournalLineDraft(code, debit, credit));
        }
        return lines;
    }

    internal static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        if (!PersianDate.TryParseDate(text, out var date))
        {
            throw new BusinessRuleException("تاریخ سند را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
        }
        return date;
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
        if (IsAdmin)
        {
            Accounts = await _manual.GetAccountsAsync(ct);
        }
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

public sealed class ManualForm
{
    public int BranchId { get; set; }

    public string? Description { get; set; }

    /// <summary>تاریخ شمسی سند. خالی یعنی امروز؛ تاریخ گذشته تا ۳۰ روز قبل مجاز است.</summary>
    public string? OccurredOn { get; set; }

    public List<ManualLineForm> Lines { get; set; } = new() { new(), new(), new(), new() };
}

public sealed class ManualLineForm
{
    public string? AccountCode { get; set; }

    public string? Debit { get; set; }

    public string? Credit { get; set; }
}
