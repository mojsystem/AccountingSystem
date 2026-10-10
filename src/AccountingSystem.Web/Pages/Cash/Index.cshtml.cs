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
    private readonly PermissionService _permissions;

    public IndexModel(CurrencyAdminService admin, BranchService branches, PermissionService permissions)
    {
        _admin = admin;
        _branches = branches;
        _permissions = permissions;
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

    /// <summary>دسترسی‌های کاربر جاری (برای نمایش دکمه‌ها در هر شعبه).</summary>
    public UserAccess? Access { get; private set; }

    /// <summary>پیام توضیحی وقتی شعبه‌ی درخواستی قابل نمایش نیست.</summary>
    public string? Notice { get; private set; }

    /// <summary>شعبه‌هایی که کاربر می‌بیند (فیلتر گزارش).</summary>
    public IReadOnlyList<BranchInfo> ReadableBranches { get; private set; } = Array.Empty<BranchInfo>();

    public bool CanAt(Permission permission, int branchId) => Access is not null && PermissionRules.IsAllowed(Access, permission, branchId);

    public IReadOnlyList<OpeningInfo> Openings { get; private set; } = Array.Empty<OpeningInfo>();

    /// <summary>ابطال موجودی افتتاحیه (مدیر یا دارنده‌ی دسترسی ابطال در همان شعبه). اگر معاملات بعدی به آن وابسته باشند، ابطال رد می‌شود.</summary>
    public async Task<IActionResult> OnPostVoidOpeningAsync(long openingId, string? voidReason, CancellationToken ct)
    {
        try
        {
            await _admin.VoidOpeningAsync(User.ToCurrentUser(), openingId, voidReason ?? string.Empty, DateTime.Now, ct);
            TempData["Success"] = $"موجودی افتتاحیه‌ی شماره {openingId} باطل شد و سند ابطال ثبت گردید.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(new { branchFilter = BranchFilter });
    }

    private static DateTime? ParseOccurredOn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        if (!PersianDate.TryParseDate(text, out var date))
        {
            throw new BusinessRuleException("تاریخ افتتاحیه را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
        }
        return date;
    }

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
            var occurredOn = ParseOccurredOn(IrrInput.OccurredOn);
            await _admin.RecordOpeningAsync(User.ToCurrentUser(), IrrInput.BranchId, CurrencyCodes.Irr, amount, null, DateTime.Now, occurredOn, ct);
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
            var occurredOn = ParseOccurredOn(FxInput.OccurredOn);
            await _admin.RecordOpeningAsync(User.ToCurrentUser(), FxInput.BranchId, FxInput.CurrencyCode, quantity, rate, DateTime.Now, occurredOn, ct);
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
        var (exportBranch, _) = WebExtensions.ReadableBranchFilter(await _permissions.GetAccessAsync(user, ct), BranchFilter);
        var boxes = await _admin.GetCashBoxesAsync(user, exportBranch, ct);
        return File(WorkbookBuilder.CashBoxes(boxes), ExcelContentType, $"cash-boxes-{DateTime.Now:yyyyMMdd}.xlsx");
    }

    private async Task<IReadOnlyList<OpeningInfo>> LoadOpeningsAsync(CancellationToken ct)
    {
        var today = DateTime.Today;
        return await _admin.GetOpeningsAsync(User.ToCurrentUser(), BranchFilter, today.AddDays(-90), today.AddDays(1), ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Openings = await LoadOpeningsAsync(ct);
        var user = User.ToCurrentUser();
        IsAdmin = user.Role == UserRole.Admin;
        var access = await _permissions.GetAccessAsync(user, ct);
        Access = access;
        var (readBranch, notice) = WebExtensions.ReadableBranchFilter(access, BranchFilter);
        Notice = notice;
        Boxes = await _admin.GetCashBoxesAsync(user, readBranch, ct);
        Branches = await _permissions.GetBranchesAsync(user, Permission.OpeningCreate, ct);
        ReadableBranches = await _permissions.GetBranchesAsync(user, null, ct);
        var all = await _admin.GetCurrenciesAsync(ct);
        Currencies = all.Where(c => c.IsActive && c.Code != CurrencyCodes.Irr).ToList();
    }
}

public sealed class IrrOpeningForm
{
    public int BranchId { get; set; }

    public string? Amount { get; set; }

    /// <summary>تاریخ شمسی موجودی افتتاحیه. خالی یعنی امروز؛ تاریخ گذشته تا ۳۰ روز قبل مجاز است.</summary>
    public string? OccurredOn { get; set; }
}

public sealed class FxOpeningForm
{
    public int BranchId { get; set; }

    public string CurrencyCode { get; set; } = "USD";

    public string? Quantity { get; set; }

    public string? UnitRate { get; set; }

    /// <summary>تاریخ شمسی موجودی افتتاحیه. خالی یعنی امروز؛ تاریخ گذشته تا ۳۰ روز قبل مجاز است.</summary>
    public string? OccurredOn { get; set; }
}
