using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Reports;

/// <summary>گزارش مانده‌ی اشخاص و معین تفصیلی مشتریان.</summary>
public sealed class IndexModel : PageModel
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly ReportService _reports;
    private readonly PermissionService _permissions;
    private readonly CustomerService _customers;

    public IndexModel(ReportService reports, PermissionService permissions, CustomerService customers)
    {
        _reports = reports;
        _permissions = permissions;
        _customers = customers;
    }

    [BindProperty(SupportsGet = true)]
    public string? ReportType { get; set; } = "BALANCES";

    [BindProperty(SupportsGet = true)]
    public int? BranchFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? CustomerId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? AsOf { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? To { get; set; }

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = Array.Empty<BranchInfo>();

    public IReadOnlyList<CustomerInfo> Customers { get; private set; } = Array.Empty<CustomerInfo>();

    public IReadOnlyList<CustomerBalanceReportRow> Balances { get; private set; } = Array.Empty<CustomerBalanceReportRow>();

    public CustomerLedgerReport? Ledger { get; private set; }

    public string? Notice { get; private set; }

    public string? RangeError { get; private set; }

    public string? ErrorMessage { get; private set; }

    public bool IsBalancesReport => string.Equals(ReportType, "BALANCES", StringComparison.Ordinal);

    public static string SourceText(string sourceType) => sourceType switch
    {
        SourceTypes.Trade => "معامله",
        SourceTypes.Opening => "موجودی افتتاحیه",
        SourceTypes.Void => "ابطال",
        SourceTypes.Adjust => "تعدیل",
        SourceTypes.Manual => "سند دستی",
        SourceTypes.BankOpening => "افتتاحیه‌ی حساب بانکی",
        _ => sourceType,
    };

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnGetExcelAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (IsBalancesReport)
        {
            var asOf = PersianDate.TryParseDate(AsOf, out var parsed) ? parsed.Date : DateTime.Now.Date;
            return File(WorkbookBuilder.CustomerBalances(Balances), ExcelContentType,
                $"customer-balances-{asOf:yyyyMMdd}.xlsx");
        }

        if (Ledger is null)
        {
            ErrorMessage = "برای خروجی معین، شخص را انتخاب کنید.";
            return Page();
        }
        return File(WorkbookBuilder.CustomerLedger(Ledger), ExcelContentType,
            $"customer-ledger-{Ledger.Customer.CustomerCode}.xlsx");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        ReportType = string.Equals(ReportType, "LEDGER", StringComparison.Ordinal) ? "LEDGER" : "BALANCES";
        var user = User.ToCurrentUser();
        Branches = await _permissions.GetBranchesAsync(user, null, ct);
        var access = await _permissions.GetAccessAsync(user, ct);
        var (scope, notice) = WebExtensions.ReadableBranchFilter(access, BranchFilter);
        Notice = notice;
        BranchFilter = scope;
        Customers = await _customers.SearchAsync(user, null, ct, 5000);

        if (IsBalancesReport)
        {
            var asOf = ResolveAsOf();
            Balances = await _reports.GetCustomerBalancesAsync(user, scope, asOf.AddDays(1), ct);
            return;
        }

        var (from, to) = ResolveRange();
        if (CustomerId is not > 0)
        {
            Ledger = null;
            return;
        }
        try
        {
            Ledger = await _reports.GetCustomerLedgerAsync(user, scope, CustomerId.Value, from, to, ct);
        }
        catch (BusinessRuleException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private DateTime ResolveAsOf()
    {
        var today = DateTime.Now.Date;
        if (string.IsNullOrWhiteSpace(AsOf))
        {
            AsOf = PersianDate.FormatDate(today);
            return today;
        }
        if (!PersianDate.TryParseDate(AsOf, out var parsed))
        {
            RangeError = "تاریخ مانده را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).";
            AsOf = PersianDate.FormatDate(today);
            return today;
        }
        AsOf = PersianDate.FormatDate(parsed);
        return parsed.Date;
    }

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
