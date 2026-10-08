using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Rates;

public class IndexModel : PageModel
{
    private readonly CurrencyAdminService _admin;

    public IndexModel(CurrencyAdminService admin)
    {
        _admin = admin;
    }

    [BindProperty]
    public RateForm RateInput { get; set; } = new();

    [BindProperty]
    public CurrencyForm CurrencyInput { get; set; } = new();

    public IReadOnlyList<CurrencyInfo> Currencies { get; private set; } = Array.Empty<CurrencyInfo>();

    public IReadOnlyList<RateInfo> RateList { get; private set; } = Array.Empty<RateInfo>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostSetRateAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (!InputParser.TryParseDecimal(RateInput.BuyRate, out var buy) || !InputParser.TryParseDecimal(RateInput.SellRate, out var sell))
        {
            ModelState.AddModelError(string.Empty, "نرخ خرید و فروش را به‌درستی وارد کنید.");
            return Page();
        }

        try
        {
            await _admin.SetRateAsync(User.ToCurrentUser(), RateInput.CurrencyCode, buy, sell, DateTime.Now, ct);
            TempData["Success"] = "نرخ جدید ثبت شد.";
            return RedirectToPage();
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostAddCurrencyAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        try
        {
            await _admin.AddCurrencyAsync(
                User.ToCurrentUser(),
                CurrencyInput.Code ?? string.Empty,
                CurrencyInput.Name ?? string.Empty,
                CurrencyInput.DecimalPlaces,
                DateTime.Now,
                ct);
            TempData["Success"] = "ارز جدید اضافه شد.";
            return RedirectToPage();
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var all = await _admin.GetCurrenciesAsync(ct);
        Currencies = all.Where(c => c.IsActive && c.Code != CurrencyCodes.Irr).ToList();
        RateList = await _admin.GetLatestRatesAsync(ct);
    }
}

public sealed class RateForm
{
    public string CurrencyCode { get; set; } = "USD";

    public string? BuyRate { get; set; }

    public string? SellRate { get; set; }
}

public sealed class CurrencyForm
{
    public string? Code { get; set; }

    public string? Name { get; set; }

    public int DecimalPlaces { get; set; } = 2;
}
