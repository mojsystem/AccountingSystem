using System.Text.Json;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.CashTransactions;

public class IndexModel : PageModel
{
    private readonly CashTransactionService _transactions;
    private readonly CurrencyAdminService _admin;
    private readonly PermissionService _permissions;
    private readonly CustomerService _customers;

    public IndexModel(
        CashTransactionService transactions,
        CurrencyAdminService admin,
        PermissionService permissions,
        CustomerService customers)
    {
        _transactions = transactions;
        _admin = admin;
        _permissions = permissions;
        _customers = customers;
    }

    [BindProperty]
    public CashTransactionForm Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? BranchFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? EditId { get; set; }

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = Array.Empty<BranchInfo>();

    public IReadOnlyList<BranchInfo> ReadableBranches { get; private set; } = Array.Empty<BranchInfo>();

    public IReadOnlyList<CurrencyInfo> Currencies { get; private set; } = Array.Empty<CurrencyInfo>();

    public IReadOnlyList<CustomerInfo> Customers { get; private set; } = Array.Empty<CustomerInfo>();

    public IReadOnlyList<CashTransactionInfo> Transactions { get; private set; } = Array.Empty<CashTransactionInfo>();

    public UserAccess? Access { get; private set; }

    public CashTransactionInfo? EditingTransaction { get; private set; }

    public bool CanEditForm { get; private set; }

    public bool IsEditing => EditingTransaction is not null;

    public bool CanAt(Permission permission, int branchId) =>
        Access is not null && PermissionRules.IsAllowed(Access, permission, branchId);

    public string RatesJson { get; private set; } = "[]";

    public string? Notice { get; private set; }

    public string? RangeError { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct, loadEditInput: true);
        if (EditId is > 0 && EditingTransaction is null)
        {
            return NotFound();
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            var amount = ParseAmount(Input.Amount, "مبلغ");
            var rateMode = Input.CurrencyCode == CurrencyCodes.Irr
                ? TradeRateMode.Derived
                : ParseRateMode(Input.RateMode);
            decimal? rate = null;
            if (Input.CurrencyCode != CurrencyCodes.Irr && rateMode == TradeRateMode.Direct)
            {
                rate = ParseAmount(Input.RateIrr, "نرخ توافقی");
            }

            if (!Enum.TryParse<CashTransactionDirection>(Input.Direction, ignoreCase: true, out var direction))
            {
                throw new BusinessRuleException("نوع دریافت یا پرداخت را انتخاب کنید.");
            }
            if (string.IsNullOrWhiteSpace(Input.CurrencyCode))
            {
                throw new BusinessRuleException("ارز را انتخاب کنید.");
            }
            DateTime? occurredOn = ParseDate(Input.OccurredOn);
            var user = User.ToCurrentUser();
            var input = new CashTransactionInput(
                Input.BranchId,
                direction,
                Input.CustomerId ?? 0,
                Input.CurrencyCode,
                amount,
                Input.CurrencyCode == CurrencyCodes.Irr ? TradeRateMode.Derived : rateMode,
                rate,
                Input.Note);

            if (EditId is > 0)
            {
                var replacementId = await _transactions.EditAsync(user, EditId.Value, input, occurredOn, DateTime.Now, ct);
                TempData["Success"] = $"دریافت/پرداخت اصلاح شد؛ نسخه‌ی جایگزین شماره {replacementId} ثبت گردید.";
            }
            else
            {
                var id = await _transactions.RecordAsync(user, input, DateTime.Now, occurredOn, ct);
                TempData["Success"] = $"سند دریافت/پرداخت شماره {id} ثبت شد.";
            }
            return RedirectToPage(new { from = From, to = To, branchFilter = BranchFilter });
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync(ct, loadEditInput: false);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostVoidAsync(long transactionId, string? voidReason, CancellationToken ct)
    {
        try
        {
            await _transactions.VoidAsync(User.ToCurrentUser(), transactionId, voidReason ?? string.Empty, DateTime.Now, ct);
            TempData["Success"] = $"دریافت/پرداخت شماره {transactionId} باطل شد و سند معکوس ثبت گردید.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(new { from = From, to = To, branchFilter = BranchFilter });
    }

    private async Task LoadAsync(CancellationToken ct, bool loadEditInput)
    {
        var user = User.ToCurrentUser();
        Access = await _permissions.GetAccessAsync(user, ct);
        Branches = await _permissions.GetBranchesAsync(user, Permission.CashTransactionCreate, ct);
        ReadableBranches = await _permissions.GetBranchesAsync(user, null, ct);
        Currencies = (await _admin.GetCurrenciesAsync(ct)).Where(c => c.IsActive).OrderBy(c => c.Code).ToList();

        try
        {
            Customers = await _customers.SearchAsync(user, null, ct, 2000);
        }
        catch (BusinessRuleException)
        {
            Customers = Array.Empty<CustomerInfo>();
        }

        if (EditId is > 0)
        {
            EditingTransaction = await _transactions.GetAsync(user, EditId.Value, ct);
            if (EditingTransaction is not null)
            {
                var transaction = EditingTransaction;
                CanEditForm = PermissionRules.IsAllowed(Access, Permission.CashTransactionEdit, transaction.BranchId) && !transaction.IsVoided;
                if (Customers.All(c => c.Id != transaction.CustomerId))
                {
                    var customer = await _customers.GetAsync(user, transaction.CustomerId, ct);
                    Customers = Customers.Append(customer).OrderBy(c => c.FullName, StringComparer.Ordinal).ToList();
                }
                if (loadEditInput)
                {
                    Input = new CashTransactionForm
                    {
                        BranchId = transaction.BranchId,
                        Direction = transaction.Direction == CashTransactionDirection.Receipt ? "Receipt" : "Payment",
                        CustomerId = transaction.CustomerId,
                        CurrencyCode = transaction.CurrencyCode,
                        Amount = transaction.Amount.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                        RateMode = transaction.RateMode == TradeRateMode.Direct ? "DIRECT" : "DERIVED",
                        RateIrr = transaction.RateIrr.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                        Note = transaction.Note,
                        OccurredOn = PersianDate.FormatDate(transaction.OccurredAt),
                    };
                }
            }
        }
        else
        {
            CanEditForm = Branches.Count > 0;
            if (Input.BranchId <= 0 && Branches.Count > 0)
            {
                Input.BranchId = Branches[0].Id;
            }
        }

        var rates = new List<RateInfo>();
        foreach (var branch in ReadableBranches)
        {
            rates.AddRange(await _admin.GetLatestRatesAsync(user, branch.Id, ct));
        }
        RatesJson = JsonSerializer.Serialize(rates.Select(r => new
        {
            branchId = r.BranchId,
            code = r.CurrencyCode,
            buy = r.BuyRateIrr,
            sell = r.SellRateIrr,
        }));

        var today = DateTime.Now;
        if (!PersianDate.TryParseRange(From, To, today, out var fromInclusive, out var toExclusive, out var error))
        {
            RangeError = error;
            fromInclusive = today.Date;
            toExclusive = today.Date.AddDays(1);
        }
        From = PersianDate.FormatDate(fromInclusive);
        To = PersianDate.FormatDate(toExclusive.AddDays(-1));

        var (readBranch, notice) = WebExtensions.ReadableBranchFilter(Access, BranchFilter);
        Notice = notice;
        Transactions = await _transactions.GetTransactionsAsync(user, readBranch, fromInclusive, toExclusive, ct);
    }

    private static decimal ParseAmount(string? text, string label)
    {
        if (!InputParser.TryParseDecimal(text, out var value))
        {
            throw new BusinessRuleException($"{label} را به‌درستی وارد کنید.");
        }
        return value;
    }

    private static TradeRateMode ParseRateMode(string? value) => value switch
    {
        "DIRECT" => TradeRateMode.Direct,
        "DERIVED" => TradeRateMode.Derived,
        _ => throw new BusinessRuleException("روش نرخ را انتخاب کنید."),
    };

    private static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        if (!PersianDate.TryParseDate(text, out var date))
        {
            throw new BusinessRuleException("تاریخ را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
        }
        return date;
    }
}

public sealed class CashTransactionForm
{
    public int BranchId { get; set; }

    public string Direction { get; set; } = "Receipt";

    public int? CustomerId { get; set; }

    public string CurrencyCode { get; set; } = CurrencyCodes.Irr;

    public string? Amount { get; set; }

    public string RateMode { get; set; } = "DERIVED";

    public string? RateIrr { get; set; }

    public string? Note { get; set; }

    public string? OccurredOn { get; set; }
}
