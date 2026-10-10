using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Reports;

/// <summary>گزارش مانده و معین اشخاص، همه‌ی سرفصل‌ها، حساب‌های بانکی و صندوق‌ها.</summary>
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
    public string? AccountCode { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool IncludeDescendants { get; set; } = true;

    [BindProperty(SupportsGet = true)]
    public int? CashBoxId { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? BankAccountId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? DateMode { get; set; } = "ASOF";

    [BindProperty(SupportsGet = true)]
    public string? AsOf { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? To { get; set; }

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = Array.Empty<BranchInfo>();

    public IReadOnlyList<CustomerInfo> Customers { get; private set; } = Array.Empty<CustomerInfo>();

    public IReadOnlyList<AccountInfo> Accounts { get; private set; } = Array.Empty<AccountInfo>();

    public IReadOnlyList<CashBoxInfo> CashBoxes { get; private set; } = Array.Empty<CashBoxInfo>();

    public IReadOnlyList<BankAccountInfo> BankAccounts { get; private set; } = Array.Empty<BankAccountInfo>();

    public IReadOnlyList<CustomerBalanceReportRow> Balances { get; private set; } = Array.Empty<CustomerBalanceReportRow>();

    public CustomerLedgerReport? Ledger { get; private set; }

    public AccountBalanceReport? AccountBalance { get; private set; }

    public AccountLedgerReport? AccountLedger { get; private set; }

    public IReadOnlyList<CashBoxLedgerReport> CashBoxLedgers { get; private set; } = Array.Empty<CashBoxLedgerReport>();

    public IReadOnlyList<BankAccountLedgerReport> BankLedgers { get; private set; } = Array.Empty<BankAccountLedgerReport>();

    public string? Notice { get; private set; }

    public string? RangeError { get; private set; }

    public string? ErrorMessage { get; private set; }

    public bool IsBalancesReport => string.Equals(ReportType, "BALANCES", StringComparison.Ordinal);

    public bool IsCustomerLedgerReport => string.Equals(ReportType, "LEDGER", StringComparison.Ordinal);

    public bool IsAccountReport => string.Equals(ReportType, "ACCOUNT", StringComparison.Ordinal);

    public bool IsCashBoxReport => string.Equals(ReportType, "CASHBOX", StringComparison.Ordinal);

    public bool IsBankReport => string.Equals(ReportType, "BANK", StringComparison.Ordinal);

    public bool IsDateRange => string.Equals(DateMode, "RANGE", StringComparison.Ordinal);

    public static string SourceText(string sourceType) => sourceType switch
    {
        SourceTypes.Trade => "معامله",
        SourceTypes.Opening => "موجودی افتتاحیه",
        SourceTypes.Void => "ابطال",
        SourceTypes.Adjust => "تعدیل",
        SourceTypes.Manual => "سند دستی",
        SourceTypes.BankOpening => "افتتاحیه‌ی حساب بانکی",
        SourceTypes.CashTransaction => "دریافت/پرداخت",
        SourceTypes.CashReceipt => "رسید دریافت",
        SourceTypes.CashPayment => "سند پرداخت",
        SourceTypes.CashAdjustment => "تعدیل دریافت/پرداخت",
        _ => sourceType,
    };

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnGetExcelAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (!string.IsNullOrWhiteSpace(ErrorMessage))
        {
            return Page();
        }

        if (IsBalancesReport)
        {
            var asOf = ResolveAsOf();
            return File(WorkbookBuilder.CustomerBalances(Balances), ExcelContentType,
                $"customer-balances-{asOf:yyyyMMdd}.xlsx");
        }

        if (IsCustomerLedgerReport)
        {
            if (Ledger is null)
            {
                ErrorMessage = "برای خروجی معین، شخص را انتخاب کنید.";
                return Page();
            }
            return File(WorkbookBuilder.CustomerLedger(Ledger), ExcelContentType,
                $"customer-ledger-{Ledger.Customer.CustomerCode}.xlsx");
        }

        if (IsAccountReport)
        {
            if (!IsDateRange && AccountBalance is not null)
            {
                return File(WorkbookBuilder.AccountBalance(AccountBalance), ExcelContentType,
                    $"account-balance-{AccountBalance.Account.Code}-{AccountBalance.AsOf:yyyyMMdd}.xlsx");
            }
            if (AccountLedger is not null)
            {
                return File(WorkbookBuilder.AccountLedger(AccountLedger), ExcelContentType,
                    $"account-ledger-{AccountLedger.Account.Code}-{AccountLedger.FromInclusive:yyyyMMdd}-{AccountLedger.ToExclusive.AddDays(-1):yyyyMMdd}.xlsx");
            }
            ErrorMessage = "برای خروجی گزارش سرفصل، یک سرفصل را انتخاب کنید.";
            return Page();
        }

        if (IsCashBoxReport)
        {
            var suffix = ReportDateSuffix();
            return File(WorkbookBuilder.CashBoxLedgers(CashBoxLedgers), ExcelContentType, $"cashbox-ledger-{suffix}.xlsx");
        }

        if (IsBankReport)
        {
            var suffix = ReportDateSuffix();
            return File(WorkbookBuilder.BankAccountLedgers(BankLedgers), ExcelContentType, $"bank-ledger-{suffix}.xlsx");
        }

        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        ReportType = ReportType switch
        {
            "LEDGER" => "LEDGER",
            "ACCOUNT" => "ACCOUNT",
            "CASHBOX" => "CASHBOX",
            "BANK" => "BANK",
            _ => "BALANCES",
        };
        DateMode = string.Equals(DateMode, "RANGE", StringComparison.Ordinal) ? "RANGE" : "ASOF";
        var user = User.ToCurrentUser();
        Branches = await _permissions.GetBranchesAsync(user, null, ct);
        var access = await _permissions.GetAccessAsync(user, ct);
        var (scope, notice) = WebExtensions.ReadableBranchFilter(access, BranchFilter);
        Notice = notice;
        BranchFilter = scope;
        Customers = await _customers.SearchAsync(user, null, ct, 5000);
        Accounts = await _reports.GetAccountReportOptionsAsync(user, ct);
        CashBoxes = await _reports.GetCashBoxReportOptionsAsync(user, scope, ct);
        BankAccounts = await _reports.GetBankAccountReportOptionsAsync(user, scope, ct);

        if (IsBalancesReport)
        {
            var asOf = ResolveAsOf();
            Balances = await _reports.GetCustomerBalancesAsync(user, scope, asOf.AddDays(1), ct);
            return;
        }

        if (IsCustomerLedgerReport)
        {
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
            return;
        }

        if (IsAccountReport)
        {
            var accountCode = AccountCode;
            if (string.IsNullOrWhiteSpace(accountCode))
            {
                return;
            }
            try
            {
                if (IsDateRange)
                {
                    var (from, to) = ResolveRange();
                    AccountLedger = await _reports.GetAccountLedgerReportAsync(
                        user, scope, accountCode, IncludeDescendants, from, to, ct);
                }
                else
                {
                    var asOf = ResolveAsOf();
                    AccountBalance = await _reports.GetAccountBalanceReportAsync(
                        user, scope, accountCode, IncludeDescendants, asOf, ct);
                }
            }
            catch (BusinessRuleException ex)
            {
                ErrorMessage = ex.Message;
            }
            return;
        }

        try
        {
            DateTime? from;
            DateTime to;
            if (IsDateRange)
            {
                var range = ResolveRange();
                from = range.From;
                to = range.To;
            }
            else
            {
                from = null;
                to = ResolveAsOf().AddDays(1);
            }

            if (IsCashBoxReport)
            {
                CashBoxLedgers = await _reports.GetCashBoxLedgerReportsAsync(
                    user, scope, CashBoxId, from, to, ct);
            }
            else if (IsBankReport)
            {
                BankLedgers = await _reports.GetBankAccountLedgerReportsAsync(
                    user, scope, BankAccountId, from, to, ct);
            }
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

    private string ReportDateSuffix()
    {
        if (!IsDateRange)
        {
            var asOf = ResolveAsOf();
            return asOf.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        }
        var (from, to) = ResolveRange();
        return $"{from:yyyyMMdd}-{to.AddDays(-1):yyyyMMdd}";
    }
}
