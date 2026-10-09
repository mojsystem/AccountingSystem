using System.Globalization;
using System.Text.Json;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Trade;

/// <summary>
/// ویرایش معامله (مدیر، یا کاربری که دسترسی «ویرایش معامله» را در شعبه‌ی خودش دارد). تغییر اطلاعات توصیفی همان سند را به‌روز می‌کند؛
/// تغییر مالی، معامله‌ی قبلی را با سند ابطال باطل می‌کند و نسخه‌ی اصلاحی جایگزین آن می‌شود.
/// </summary>
public class EditModel : PageModel
{
    private readonly CurrencyTradeService _trades;
    private readonly CurrencyAdminService _admin;
    private readonly PermissionService _permissions;
    private readonly CustomerService _customers;

    public EditModel(CurrencyTradeService trades, CurrencyAdminService admin, PermissionService permissions, CustomerService customers)
    {
        _customers = customers;
        _trades = trades;
        _admin = admin;
        _permissions = permissions;
    }

    [BindProperty]
    public TradeForm Input { get; set; } = new();

    public long Id { get; private set; }

    public bool IsVoided { get; private set; }

    /// <summary>کاربر جاری اجازه‌ی ویرایش این معامله را دارد (مدیر، یا دسترسی «ویرایش معامله» در همان شعبه).</summary>
    public bool CanEdit { get; private set; }

    public IReadOnlyList<CurrencyInfo> Currencies { get; private set; } = Array.Empty<CurrencyInfo>();

    public IReadOnlyList<CurrencyInfo> SettlementCurrencies { get; private set; } = Array.Empty<CurrencyInfo>();

    public string RatesJson { get; private set; } = "{}";

    /// <summary>مشتریان مشترک برای انتخاب مشتری معامله.</summary>
    public IReadOnlyList<CustomerInfo> Customers { get; private set; } = Array.Empty<CustomerInfo>();

    public async Task<IActionResult> OnGetAsync(long id, CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        var trade = await _trades.GetTradeAsync(user, id, ct);
        if (trade is null)
        {
            return NotFound();
        }

        Id = trade.Id;
        IsVoided = trade.IsVoided;
        CanEdit = await _permissions.HasAsync(user, Permission.TradeEdit, trade.BranchId, ct);
        Input = new TradeForm
        {
            TradeType = trade.Type == TradeType.Sell ? "SELL" : "BUY",
            BranchId = trade.BranchId,
            CurrencyCode = trade.CurrencyCode,
            Amount = trade.Amount.ToString("0.####", CultureInfo.InvariantCulture),
            Rate = trade.Rate.ToString("0.####", CultureInfo.InvariantCulture),
            Fee = trade.FeeIrr.ToString("0", CultureInfo.InvariantCulture),
            CustomerId = trade.CustomerId,
            Note = trade.Note,
            SettlementMode = trade.SettlementMode switch
            {
                TradeSettlementMode.Direct => "DIRECT",
                TradeSettlementMode.Split => "SPLIT",
                TradeSettlementMode.CustomerAccount => "ACCOUNT",
                _ => "DIRECT",
            },
            RateMode = trade.RateMode == TradeRateMode.Direct ? "DIRECT" : "DERIVED",
            SettlementCurrencyCode = trade.SettlementCurrencyCode ?? CurrencyCodes.Irr,
            CrossRate = trade.CrossRate > 0m ? trade.CrossRate.ToString("0.########", CultureInfo.InvariantCulture) : null,
            ApplyCustomerOffset = trade.CustomerOffsetIrr > 0m,
            SettlementLines = (trade.Settlements ?? Array.Empty<TradeSettlementInfo>())
                .Select(line => new TradeSettlementForm
                {
                    CurrencyCode = line.CurrencyCode,
                    Amount = line.Amount.ToString("0.####", CultureInfo.InvariantCulture),
                }).ToList(),
            OccurredOn = PersianDate.FormatDate(trade.OccurredAt),
        };
        EnsureSettlementRows();
        Currencies = await LoadCurrenciesAsync(ct);
        SettlementCurrencies = await LoadSettlementCurrenciesAsync(ct);
        RatesJson = await LoadRatesJsonAsync(user, trade.BranchId, ct);
        Customers = await LoadCustomersAsync(user, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long id, CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        Id = id;

        DateTime? occurredOn = null;
        if (!string.IsNullOrWhiteSpace(Input.OccurredOn))
        {
            if (!PersianDate.TryParseDate(Input.OccurredOn, out var parsedDate))
            {
                return await ShowErrorAsync("تاریخ معامله را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).", id, ct);
            }
            occurredOn = parsedDate;
        }

        try
        {
            var input = Input.ToTradeInput();
            var type = Input.TradeType == "SELL" ? TradeType.Sell : TradeType.Buy;
            var newId = await _trades.EditTradeAsync(user, id, input, type, occurredOn, DateTime.Now, ct);
            TempData["Success"] = newId is null
                ? $"اطلاعات توصیفی معامله شماره {id} به‌روز شد."
                : $"معامله‌ی شماره {id} باطل شد و نسخه‌ی اصلاحی شماره {newId} جایگزین آن شد.";
            return RedirectToPage("Index");
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            return await ShowErrorAsync(ex.Message, id, ct);
        }
    }

    private async Task<IActionResult> ShowErrorAsync(string message, long id, CancellationToken ct)
    {
        ModelState.AddModelError(string.Empty, message);
        var user = User.ToCurrentUser();
        var trade = await _trades.GetTradeAsync(user, id, ct);
        IsVoided = trade?.IsVoided ?? false;
        CanEdit = trade is not null && await _permissions.HasAsync(user, Permission.TradeEdit, trade.BranchId, ct);
        EnsureSettlementRows();
        Currencies = await LoadCurrenciesAsync(ct);
        SettlementCurrencies = await LoadSettlementCurrenciesAsync(ct);
        RatesJson = trade is null ? "{}" : await LoadRatesJsonAsync(user, trade.BranchId, ct);
        Customers = await LoadCustomersAsync(user, ct);
        return Page();
    }

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

    private async Task<IReadOnlyList<CurrencyInfo>> LoadCurrenciesAsync(CancellationToken ct) =>
        (await _admin.GetCurrenciesAsync(ct)).Where(c => c.IsActive && c.Code != CurrencyCodes.Irr).ToList();

    private async Task<IReadOnlyList<CurrencyInfo>> LoadSettlementCurrenciesAsync(CancellationToken ct) =>
        (await _admin.GetCurrenciesAsync(ct)).Where(c => c.IsActive).ToList();

    private async Task<string> LoadRatesJsonAsync(CurrentUser user, int branchId, CancellationToken ct)
    {
        var rates = await _admin.GetLatestRatesAsync(user, branchId, ct);
        return JsonSerializer.Serialize(rates
            .GroupBy(r => r.BranchId.ToString(CultureInfo.InvariantCulture))
            .ToDictionary(
                group => group.Key,
                group => group.Select(rate => new { code = rate.CurrencyCode, buy = rate.BuyRateIrr, sell = rate.SellRateIrr }).ToList()));
    }

    private void EnsureSettlementRows()
    {
        Input.SettlementLines ??= new List<TradeSettlementForm>();
        while (Input.SettlementLines.Count < 2)
        {
            Input.SettlementLines.Add(new TradeSettlementForm());
        }
    }
}
