using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Cash;

public class IndexModel : PageModel
{
    private readonly CurrencyAdminService _admin;

    public IndexModel(CurrencyAdminService admin)
    {
        _admin = admin;
    }

    [BindProperty]
    public IrrOpeningForm IrrInput { get; set; } = new();

    [BindProperty]
    public FxOpeningForm FxInput { get; set; } = new();

    public IReadOnlyList<CashBoxInfo> Boxes { get; private set; } = Array.Empty<CashBoxInfo>();

    public IReadOnlyList<CurrencyInfo> Currencies { get; private set; } = Array.Empty<CurrencyInfo>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostOpeningIrrAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (!InputParser.TryParseDecimal(IrrInput.Amount, out var amount))
        {
            ModelState.AddModelError(string.Empty, "مبلغ را به‌درستی وارد کنید.");
            return Page();
        }

        try
        {
            await _admin.OpeningIrrAsync(User.ToCurrentUser(), amount, DateTime.Now, ct);
            TempData["Success"] = "موجودی افتتاحیه ریال ثبت شد.";
            return RedirectToPage();
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostOpeningFxAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (!InputParser.TryParseDecimal(FxInput.Quantity, out var quantity) || !InputParser.TryParseDecimal(FxInput.UnitRate, out var rate))
        {
            ModelState.AddModelError(string.Empty, "مقدار و نرخ را به‌درستی وارد کنید.");
            return Page();
        }

        try
        {
            await _admin.OpeningForeignAsync(User.ToCurrentUser(), FxInput.CurrencyCode, quantity, rate, DateTime.Now, ct);
            TempData["Success"] = "موجودی افتتاحیه ارز ثبت شد.";
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
        Boxes = await _admin.GetCashBoxesAsync(ct);
        var all = await _admin.GetCurrenciesAsync(ct);
        Currencies = all.Where(c => c.IsActive && c.Code != CurrencyCodes.Irr).ToList();
    }
}

public sealed class IrrOpeningForm
{
    public string? Amount { get; set; }
}

public sealed class FxOpeningForm
{
    public string CurrencyCode { get; set; } = "USD";

    public string? Quantity { get; set; }

    public string? UnitRate { get; set; }
}
