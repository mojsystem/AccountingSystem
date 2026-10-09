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

    public IndexModel(CurrencyTradeService trades, CurrencyAdminService admin, ReportService reports, BranchService branches, PermissionService permissions)
    {
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

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = Array.Empty<BranchInfo>();

    public IReadOnlyList<TradeInfo> Trades { get; private set; } = Array.Empty<TradeInfo>();

    public string RatesJson { get; private set; } = "{}";

    public bool IsAdmin { get; private set; }

    /// <summary>دسترسی ویرایش معامله برای کاربر جاری (مدیر همه را دارد).</summary>
    public bool CanEditTrades { get; private set; }

    /// <summary>دسترسی ابطال معامله برای کاربر جاری (مدیر همه را دارد).</summary>
    public bool CanVoidTrades { get; private set; }

    public string? RangeError { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        if (!InputParser.TryParseDecimal(Input.Amount, out var amount))
        {
            ModelState.AddModelError(string.Empty, "مقدار ارز را به‌درستی وارد کنید.");
            await LoadAsync(ct);
            return Page();
        }
        if (!InputParser.TryParseDecimal(Input.Rate, out var rate))
        {
            ModelState.AddModelError(string.Empty, "نرخ را به‌درستی وارد کنید.");
            await LoadAsync(ct);
            return Page();
        }
        decimal fee = 0m;
        if (!string.IsNullOrWhiteSpace(Input.Fee) && !InputParser.TryParseDecimal(Input.Fee, out fee))
        {
            ModelState.AddModelError(string.Empty, "کارمزد را به‌درستی وارد کنید (عدد ریال).");
            await LoadAsync(ct);
            return Page();
        }

        var branchId = user.Role == UserRole.Admin ? Input.BranchId : user.BranchId ?? 0;
        var input = new TradeInput(branchId, Input.CurrencyCode, amount, rate, Input.CustomerName, Input.NationalCode, Input.Note, fee);
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

    /// <summary>ابطال معامله (فقط مدیر). سند معکوس ثبت می‌شود و سطر معامله حذف نمی‌شود.</summary>
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
        var trades = await _reports.GetTradesAsync(user, user.ScopeFor(BranchFilter), from, to, ct);
        var bytes = WorkbookBuilder.Trades(trades);
        return File(bytes, ExcelContentType, $"trades-{from:yyyyMMdd}-{to.AddDays(-1):yyyyMMdd}.xlsx");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        IsAdmin = user.Role == UserRole.Admin;
        var permissions = await _permissions.GetPermissionsAsync(user, ct);
        CanEditTrades = permissions.Contains(Permission.TradeEdit);
        CanVoidTrades = permissions.Contains(Permission.TradeVoid);

        Currencies = (await _admin.GetCurrenciesAsync(ct))
            .Where(c => c.IsActive && c.Code != CurrencyCodes.Irr)
            .ToList();
        Branches = await _branches.GetBranchesAsync(ct);

        // نرخ‌های هر شعبه‌ی قابل ثبت، برای پر کردن خودکار نرخ در فرم.
        var rates = await _admin.GetLatestRatesAsync(user, null, ct);
        RatesJson = JsonSerializer.Serialize(rates
            .GroupBy(r => r.BranchId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => new { code = r.CurrencyCode, buy = r.BuyRateIrr, sell = r.SellRateIrr }).ToList()));

        var (from, to) = ResolveRange();
        Trades = await _reports.GetTradesAsync(user, user.ScopeFor(BranchFilter), from, to, ct);
    }

    /// <summary>بازه‌ی نمایش معاملات. خالی بودن تاریخ یعنی امروز؛ خطای قالب در RangeError نمایش داده می‌شود.</summary>
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

public sealed class TradeForm
{
    public string TradeType { get; set; } = "BUY";

    public int BranchId { get; set; }

    public string CurrencyCode { get; set; } = "USD";

    public string? Amount { get; set; }

    public string? Rate { get; set; }

    public string? Fee { get; set; }

    public string? CustomerName { get; set; }

    public string? NationalCode { get; set; }

    public string? Note { get; set; }

    /// <summary>تاریخ شمسی معامله. خالی یعنی امروز؛ تاریخ گذشته تا ۳۰ روز قبل مجاز است.</summary>
    public string? OccurredOn { get; set; }
}
