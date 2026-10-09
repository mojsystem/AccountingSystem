using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Cash;

public class IndexModel : PageModel
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly CurrencyAdminService _admin;
    private readonly BranchService _branches;

    public IndexModel(CurrencyAdminService admin, BranchService branches)
    {
        _admin = admin;
        _branches = branches;
    }

    [BindProperty]
    public IrrOpeningForm IrrInput { get; set; } = new();

    [BindProperty]
    public FxOpeningForm FxInput { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public int? BranchFilter { get; set; }

    public IReadOnlyList<CashBoxInfo> Boxes { get; private set; } = Array.Empty<CashBoxInfo>();

    public IReadOnlyList<CurrencyInfo> Currencies { get; private set; } = Array.Empty<CurrencyInfo>();

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = Array.Empty<BranchInfo>();

    public bool IsAdmin { get; private set; }

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
            await _admin.OpeningIrrAsync(User.ToCurrentUser(), IrrInput.BranchId, amount, DateTime.Now, ct);
            TempData["Success"] = "موجودی افتتاحیه ریال ثبت شد.";
            return RedirectToPage(new { branchFilter = BranchFilter });
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
            await _admin.OpeningForeignAsync(User.ToCurrentUser(), FxInput.BranchId, FxInput.CurrencyCode, quantity, rate, DateTime.Now, ct);
            TempData["Success"] = "موجودی افتتاحیه ارز ثبت شد.";
            return RedirectToPage(new { branchFilter = BranchFilter });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnGetExcelAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        var boxes = await _admin.GetCashBoxesAsync(user, user.ScopeFor(BranchFilter), ct);
        return File(WorkbookBuilder.CashBoxes(boxes), ExcelContentType, $"cash-boxes-{DateTime.Now:yyyyMMdd}.xlsx");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        IsAdmin = user.Role == UserRole.Admin;
        Boxes = await _admin.GetCashBoxesAsync(user, user.ScopeFor(BranchFilter), ct);
        Branches = await _branches.GetBranchesAsync(ct);
        var all = await _admin.GetCurrenciesAsync(ct);
        Currencies = all.Where(c => c.IsActive && c.Code != CurrencyCodes.Irr).ToList();
    }
}

public sealed class IrrOpeningForm
{
    public int BranchId { get; set; }

    public string? Amount { get; set; }
}

public sealed class FxOpeningForm
{
    public int BranchId { get; set; }

    public string CurrencyCode { get; set; } = "USD";

    public string? Quantity { get; set; }

    public string? UnitRate { get; set; }
}
