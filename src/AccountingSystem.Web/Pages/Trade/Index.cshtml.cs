using System.Text.Json;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Trade;

public class IndexModel : PageModel
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly CurrencyTradeService _trades;
    private readonly CurrencyAdminService _admin;
    private readonly ReportService _reports;
    private readonly BranchService _branches;
    private readonly PermissionService _permissions;
    private readonly CustomerService _customers;

    public IndexModel(CurrencyTradeService trades, CurrencyAdminService admin, ReportService reports, BranchService branches, PermissionService permissions, CustomerService customers)
    {
        _customers = customers;
        _trades = trades;
        _admin = admin;
        _reports = reports;
        _branches = branches;
        _permissions = permissions;
    }

    [BindProperty]
    public TradeForm Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? BranchFilter { get; set; }

    public IReadOnlyList<CurrencyInfo> Currencies { get; private set; } = Array.Empty<CurrencyInfo>();

    public IReadOnlyList<CurrencyInfo> SettlementCurrencies { get; private set; } = Array.Empty<CurrencyInfo>();

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = Array.Empty<BranchInfo>();

    /// <summary>مشتریان مشترک برای انتخاب در فرم معامله (هر معامله باید مشتری داشته باشد).</summary>
    public IReadOnlyList<CustomerInfo> Customers { get; private set; } = Array.Empty<CustomerInfo>();

    public IReadOnlyList<TradeInfo> Trades { get; private set; } = Array.Empty<TradeInfo>();

    public string RatesJson { get; private set; } = "{}";

    public bool IsAdmin { get; private set; }

    /// <summary>دسترسی‌های کاربر جاری (برای نمایش دکمه‌ها در هر شعبه).</summary>
    public UserAccess? Access { get; private set; }

    /// <summary>پیام توضیحی وقتی شعبه‌ی درخواستی قابل نمایش نیست.</summary>
    public string? Notice { get; private set; }

    /// <summary>شعبه‌هایی که کاربر می‌بیند (فیلتر گزارش).</summary>
    public IReadOnlyList<BranchInfo> ReadableBranches { get; private set; } = Array.Empty<BranchInfo>();

    public bool CanEditAt(int branchId) => Access is not null && PermissionRules.IsAllowed(Access, Permission.TradeEdit, branchId);

    public bool CanVoidAt(int branchId) => Access is not null && PermissionRules.IsAllowed(Access, Permission.TradeVoid, branchId);

    public string? RangeError { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        DateTime? occurredOn = null;
        if (!string.IsNullOrWhiteSpace(Input.OccurredOn))
        {
            if (!PersianDate.TryParseDate(Input.OccurredOn, out var parsedDate))
            {
                ModelState.AddModelError(string.Empty, "تاریخ معامله را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
                await LoadAsync(ct);
                return Page();
            }
            occurredOn = parsedDate;
        }
        try
        {
            var input = Input.ToTradeInput();
            var type = Input.TradeType == "SELL" ? TradeType.Sell : TradeType.Buy;
            var id = await _trades.RecordTradeAsync(input, type, user, DateTime.Now, occurredOn, ct);
            TempData["Success"] = $"معامله شماره {id} با موفقیت ثبت شد.";
            return RedirectToPage(new { from = From, to = To, branchFilter = BranchFilter });
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    /// <summary>ابطال معامله (مدیر، یا دارنده‌ی دسترسی «ابطال معامله» در همان شعبه). سند معکوس ثبت می‌شود و سطر معامله حذف نمی‌شود.</summary>
    public async Task<IActionResult> OnPostVoidAsync(long voidTradeId, string? voidReason, CancellationToken ct)
    {
        try
        {
            await _trades.VoidTradeAsync(User.ToCurrentUser(), voidTradeId, voidReason ?? string.Empty, DateTime.Now, ct);
            TempData["Success"] = $"معامله شماره {voidTradeId} باطل شد و سند ابطال ثبت گردید.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(new { from = From, to = To, branchFilter = BranchFilter });
    }

    public async Task<IActionResult> OnGetExcelAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        var (from, to) = ResolveRange();
        var access = await _permissions.GetAccessAsync(user, ct);
        var (branch, _) = WebExtensions.ReadableBranchFilter(access, BranchFilter);
        var trades = await _reports.GetTradesAsync(user, branch, from, to, ct);
        var bytes = WorkbookBuilder.Trades(trades);
        return File(bytes, ExcelContentType, $"trades-{from:yyyyMMdd}-{to.AddDays(-1):yyyyMMdd}.xlsx");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        IsAdmin = user.Role == UserRole.Admin;
        var access = await _permissions.GetAccessAsync(user, ct);
        Access = access;

        var activeCurrencies = (await _admin.GetCurrenciesAsync(ct)).Where(c => c.IsActive).ToList();
        Currencies = activeCurrencies.Where(c => c.Code != CurrencyCodes.Irr).ToList();
        SettlementCurrencies = activeCurrencies;
        Branches = await _permissions.GetBranchesAsync(user, Permission.TradeRecord, ct);
        Customers = await LoadCustomersAsync(user, ct);
        ReadableBranches = await _permissions.GetBranchesAsync(user, null, ct);

        // نرخ‌های هر شعبه‌ی قابل ثبت، برای پر کردن خودکار نرخ در فرم.
        var rates = new List<RateInfo>();
        foreach (var formBranch in Branches)
        {
            rates.AddRange(await _admin.GetLatestRatesAsync(user, formBranch.Id, ct));
        }
        RatesJson = JsonSerializer.Serialize(rates
            .GroupBy(r => r.BranchId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => new { code = r.CurrencyCode, buy = r.BuyRateIrr, sell = r.SellRateIrr }).ToList()));

        var (from, to) = ResolveRange();
        var (readBranch, notice) = WebExtensions.ReadableBranchFilter(access, BranchFilter);
        Notice = notice;
        Trades = await _reports.GetTradesAsync(user, readBranch, from, to, ct);
    }

    /// <summary>بازه‌ی نمایش معاملات. خالی بودن تاریخ یعنی امروز؛ خطای قالب در RangeError نمایش داده می‌شود.</summary>
    private async Task<IReadOnlyList<CustomerInfo>> LoadCustomersAsync(CurrentUser user, CancellationToken ct)
    {
        try
        {
            return await _customers.SearchAsync(user, null, ct, 2000);
        }
        catch (BusinessRuleException)
        {
            return Array.Empty<CustomerInfo>();
        }
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

    public static string SettlementModeText(TradeSettlementMode mode) => mode switch
    {
        TradeSettlementMode.Direct => "مستقیم",
        TradeSettlementMode.Split => "چندبخشی",
        TradeSettlementMode.CustomerAccount => "حساب مشتری",
        _ => "نامشخص",
    };

    public static string SettlementSummary(TradeInfo trade)
    {
        var lines = trade.Settlements ?? Array.Empty<TradeSettlementInfo>();
        if (lines.Count == 0 && trade.Settlements is null && trade.SettlementMode == TradeSettlementMode.Direct)
        {
            var legacyAmount = trade.Type == TradeType.Buy ? trade.IrrAmount - trade.FeeIrr : trade.IrrAmount + trade.FeeIrr;
            return (trade.Type == TradeType.Buy ? "پرداخت " : "دریافت ") + MoneyMath.FormatAmount(legacyAmount, 0) + " IRR";
        }

        var parts = lines.Select(line =>
            $"{(line.Direction == TradeSettlementDirection.Payment ? "پرداخت" : "دریافت")} {MoneyMath.FormatAmount(line.Amount, 4)} {line.CurrencyCode}");
        var summary = string.Join("؛ ", parts);
        if (trade.CustomerOffsetIrr > 0m)
        {
            summary = (summary.Length == 0 ? string.Empty : summary + "؛ ") + "تهاتر " + MoneyMath.FormatAmount(trade.CustomerOffsetIrr, 0) + " ریال";
        }
        if (trade.SettlementMode == TradeSettlementMode.CustomerAccount)
        {
            var accountText = "روی حساب مشتری · " + (trade.SettlementCurrencyCode ?? CurrencyCodes.Irr);
            return summary.Length == 0 ? accountText : summary + "؛ " + accountText;
        }
        if (trade.SettlementMode == TradeSettlementMode.Split)
        {
            var due = trade.Type == TradeType.Buy ? trade.IrrAmount - trade.FeeIrr : trade.IrrAmount + trade.FeeIrr;
            var remaining = due - trade.CustomerOffsetIrr - lines.Sum(line => line.IrrAmount);
            if (remaining > 0m)
            {
                summary += (summary.Length == 0 ? string.Empty : "؛ ") + "مانده‌ی حساب " + (trade.SettlementCurrencyCode ?? CurrencyCodes.Irr);
            }
        }
        return summary.Length == 0 ? "بدون وجه نقد" : summary;
    }
}

public sealed class TradeForm
{
    public string TradeType { get; set; } = "BUY";

    public int BranchId { get; set; }

    public string CurrencyCode { get; set; } = "USD";

    public string? Amount { get; set; }

    public string? Rate { get; set; }

    public string? Fee { get; set; }

    public int? CustomerId { get; set; }

    public string? Note { get; set; }

    public string SettlementMode { get; set; } = "DIRECT";

    public string RateMode { get; set; } = "DERIVED";

    public string SettlementCurrencyCode { get; set; } = CurrencyCodes.Irr;

    public string? CrossRate { get; set; }

    public bool ApplyCustomerOffset { get; set; }

    public List<TradeSettlementForm> SettlementLines { get; set; } = new() { new(), new() };

    /// <summary>تاریخ شمسی معامله. خالی یعنی امروز؛ تاریخ گذشته تا ۳۰ روز قبل مجاز است.</summary>
    public string? OccurredOn { get; set; }

    public TradeInput ToTradeInput()
    {
        if (!InputParser.TryParseDecimal(Amount, out var amount))
        {
            throw new BusinessRuleException("مقدار ارز را به‌درستی وارد کنید.");
        }
        decimal fee = 0m;
        if (!string.IsNullOrWhiteSpace(Fee) && !InputParser.TryParseDecimal(Fee, out fee))
        {
            throw new BusinessRuleException("کارمزد را به‌درستی وارد کنید (عدد ریال).");
        }

        var mode = (SettlementMode ?? "DIRECT").Trim().ToUpperInvariant() switch
        {
            "DIRECT" => TradeSettlementMode.Direct,
            "SPLIT" => TradeSettlementMode.Split,
            "ACCOUNT" => TradeSettlementMode.CustomerAccount,
            _ => throw new BusinessRuleException("روش تسویه‌ی معامله معتبر نیست."),
        };
        var rateMode = mode == TradeSettlementMode.Direct
            ? (RateMode ?? "DERIVED").Trim().ToUpperInvariant() switch
            {
                "DIRECT" => TradeRateMode.Direct,
                "DERIVED" => TradeRateMode.Derived,
                _ => throw new BusinessRuleException("روش تعیین نرخ جفت‌ارز معتبر نیست."),
            }
            : TradeRateMode.Derived;

        var hasRate = InputParser.TryParseDecimal(Rate, out var rate);
        if (!hasRate && (mode != TradeSettlementMode.Direct || !string.IsNullOrWhiteSpace(Rate)))
        {
            throw new BusinessRuleException("نرخ را به‌درستی وارد کنید.");
        }
        if (!hasRate)
        {
            rate = 0m;
        }

        decimal? crossRate = null;
        if (mode == TradeSettlementMode.Direct && rateMode == TradeRateMode.Direct)
        {
            if (!InputParser.TryParseDecimal(CrossRate, out var parsedCrossRate))
            {
                throw new BusinessRuleException("نرخ مستقیم جفت‌ارز را وارد کنید.");
            }
            crossRate = parsedCrossRate;
        }

        var lines = new List<TradeSettlementInput>();
        if (mode == TradeSettlementMode.Split)
        {
            foreach (var line in SettlementLines ?? new List<TradeSettlementForm>())
            {
                var code = (line.CurrencyCode ?? string.Empty).Trim().ToUpperInvariant();
                if (code.Length == 0 && string.IsNullOrWhiteSpace(line.Amount))
                {
                    continue;
                }
                if (code.Length == 0)
                {
                    throw new BusinessRuleException("برای هر سطر تسویه‌ی چندبخشی، ارز را انتخاب کنید.");
                }
                if (!InputParser.TryParseDecimal(line.Amount, out var lineAmount))
                {
                    throw new BusinessRuleException($"مقدار سطر تسویه‌ی {code} را وارد کنید.");
                }
                lines.Add(new TradeSettlementInput(code, lineAmount));
            }
        }

        return new TradeInput(
            BranchId,
            (CurrencyCode ?? string.Empty).Trim().ToUpperInvariant(),
            amount,
            rate,
            null,
            null,
            Note,
            fee,
            CustomerId,
            mode,
            rateMode,
            SettlementCurrencyCode,
            crossRate,
            lines,
            ApplyCustomerOffset);
    }
}

public sealed class TradeSettlementForm
{
    public string? CurrencyCode { get; set; }

    public string? Amount { get; set; }
}
