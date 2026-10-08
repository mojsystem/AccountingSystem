using System.Text.Json;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Trade;

public class IndexModel : PageModel
{
    private readonly CurrencyTradeService _trades;
    private readonly CurrencyAdminService _admin;
    private readonly ReportService _reports;

    public IndexModel(CurrencyTradeService trades, CurrencyAdminService admin, ReportService reports)
    {
        _trades = trades;
        _admin = admin;
        _reports = reports;
    }

    [BindProperty]
    public TradeForm Input { get; set; } = new();

    public IReadOnlyList<CurrencyInfo> Currencies { get; private set; } = Array.Empty<CurrencyInfo>();

    public IReadOnlyList<TradeInfo> TodayTrades { get; private set; } = Array.Empty<TradeInfo>();

    public string RatesJson { get; private set; } = "[]";

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (!InputParser.TryParseDecimal(Input.Amount, out var amount))
        {
            ModelState.AddModelError(string.Empty, "مقدار ارز را به‌درستی وارد کنید.");
            return Page();
        }
        if (!InputParser.TryParseDecimal(Input.Rate, out var rate))
        {
            ModelState.AddModelError(string.Empty, "نرخ را به‌درستی وارد کنید.");
            return Page();
        }

        var input = new TradeInput(Input.CurrencyCode, amount, rate, Input.CustomerName, Input.NationalCode, Input.Note);
        try
        {
            var user = User.ToCurrentUser();
            var id = Input.TradeType == "SELL"
                ? await _trades.SellToCustomerAsync(input, user, DateTime.Now, ct)
                : await _trades.BuyFromCustomerAsync(input, user, DateTime.Now, ct);
            TempData["Success"] = $"معامله شماره {id} با موفقیت ثبت شد.";
            return RedirectToPage();
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var all = await _admin.GetCurrenciesAsync(ct);
        Currencies = all.Where(c => c.IsActive && c.Code != CurrencyCodes.Irr).ToList();

        var rates = await _admin.GetLatestRatesAsync(ct);
        RatesJson = JsonSerializer.Serialize(rates.Select(r => new { code = r.CurrencyCode, buy = r.BuyRateIrr, sell = r.SellRateIrr }));

        var today = DateTime.Now.Date;
        TodayTrades = await _reports.GetTradesAsync(today, today.AddDays(1), ct);
    }
}

public sealed class TradeForm
{
    public string TradeType { get; set; } = "BUY";

    public string CurrencyCode { get; set; } = "USD";

    public string? Amount { get; set; }

    public string? Rate { get; set; }

    public string? CustomerName { get; set; }

    public string? NationalCode { get; set; }

    public string? Note { get; set; }
}
