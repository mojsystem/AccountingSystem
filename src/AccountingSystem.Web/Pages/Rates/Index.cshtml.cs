using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Rates;

public class IndexModel : PageModel
{
    private readonly CurrencyAdminService _admin;
    private readonly BranchService _branches;

    public IndexModel(CurrencyAdminService admin, BranchService branches)
    {
        _admin = admin;
        _branches = branches;
    }

    [BindProperty]
    public RateForm RateInput { get; set; } = new();

    [BindProperty]
    public CurrencyForm CurrencyInput { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public int? BranchFilter { get; set; }

    public IReadOnlyList<CurrencyInfo> Currencies { get; private set; } = Array.Empty<CurrencyInfo>();

    public IReadOnlyList<RateInfo> RateList { get; private set; } = Array.Empty<RateInfo>();

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = Array.Empty<BranchInfo>();

    public bool IsAdmin { get; private set; }

    /// <summary>شعبه‌هایی که کاربر می‌بیند (فیلتر نرخ‌ها).</summary>
    public IReadOnlyList<BranchInfo> ReadableBranches { get; private set; } = Array.Empty<BranchInfo>();

    public string? Notice { get; private set; }

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
            await _admin.SetRateAsync(User.ToCurrentUser(), RateInput.BranchId, RateInput.CurrencyCode, buy, sell, DateTime.Now, ct);
            TempData["Success"] = "نرخ جدید ثبت شد.";
            return RedirectToPage(new { branchFilter = RateInput.BranchId });
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
            TempData["Success"] = "ارز جدید برای همه‌ی شعبه‌ها اضافه شد.";
            return RedirectToPage(new { branchFilter = BranchFilter });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        IsAdmin = user.Role == UserRole.Admin;
        var all = await _admin.GetCurrenciesAsync(ct);
        Currencies = all.Where(c => c.IsActive && c.Code != CurrencyCodes.Irr).ToList();
        Branches = await _permissions.GetBranchesAsync(user, Permission.RateSet, ct);
        ReadableBranches = await _permissions.GetBranchesAsync(user, null, ct);
        var access = await _permissions.GetAccessAsync(user, ct);
        var (readBranch, notice) = WebExtensions.ReadableBranchFilter(access, BranchFilter);
        Notice = notice;
        RateList = await _admin.GetLatestRatesAsync(user, readBranch, ct);
        // شعبه‌ی پیش‌فرض فرم نرخ: شعبه‌ی فیلتر (اگر قابل ثبت است) یا اولین شعبه‌ی قابل ثبت.
        if (RateInput.BranchId == 0)
        {
            RateInput.BranchId = Branches.FirstOrDefault(b => b.Id == BranchFilter)?.Id ?? Branches.FirstOrDefault()?.Id ?? 0;
        }
    }
}

public sealed class RateForm
{
    public int BranchId { get; set; }

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
