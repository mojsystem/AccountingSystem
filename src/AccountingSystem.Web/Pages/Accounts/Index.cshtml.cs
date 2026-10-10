using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Accounts;

/// <summary>سرفصل حساب‌ها؛ فقط مدیر سیستم (پوشه‌ی /Accounts در Program.cs محدود شده است).</summary>
public class IndexModel : PageModel
{
    private readonly AccountService _accounts;

    public IndexModel(AccountService accounts)
    {
        _accounts = accounts;
    }

    /// <summary>کد حسابی که در فرم ویرایش باز است؛ خالی یعنی فرم حساب تازه.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Edit { get; set; }

    [BindProperty]
    public AccountForm Form { get; set; } = new();

    public IReadOnlyList<AccountInfo> Accounts { get; private set; } = Array.Empty<AccountInfo>();

    /// <summary>حساب باز در فرم ویرایش (اگر باشد).</summary>
    public AccountInfo? Current { get; private set; }

    /// <summary>حساب‌هایی که می‌توانند پدر باشند: غیرسیستمی، فعال، کمتر از سطح تفصیلی و غیر از خود حساب.</summary>
    public IReadOnlyList<AccountInfo> ParentCandidates => Accounts
        .Where(a => !a.IsSystem && a.IsActive && a.Level < AccountRules.MaxLevel && a.Code != Current?.Code)
        .ToList();

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (Current is not null)
        {
            Form = AccountForm.From(Current);
        }
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        try
        {
            await _accounts.CreateAsync(User.ToCurrentUser(), Form.Code, Form.Name, Form.ParentCode, Form.AccountType, DateTime.Now, ct);
            TempData["Success"] = "حساب تازه ثبت شد.";
            return RedirectToPage();
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostUpdateAsync(CancellationToken ct)
    {
        var originalCode = Form.OriginalCode ?? string.Empty;
        try
        {
            await _accounts.UpdateAsync(User.ToCurrentUser(), originalCode, Form.Code, Form.Name, Form.ParentCode,
                Form.AccountType, Form.IsActive, DateTime.Now, ct);
            TempData["Success"] = "تغییرات حساب ذخیره شد.";
            return RedirectToPage();
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            Edit = originalCode;
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(string code, CancellationToken ct)
    {
        try
        {
            await _accounts.DeleteAsync(User.ToCurrentUser(), code, DateTime.Now, ct);
            TempData["Success"] = $"حساب {code} حذف شد.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Accounts = (await _accounts.GetAccountsAsync(User.ToCurrentUser(), ct))
            .OrderBy(a => a.Code, StringComparer.Ordinal)
            .ToList();
        var editCode = Edit ?? Form.OriginalCode;
        Current = string.IsNullOrEmpty(editCode) ? null : Accounts.FirstOrDefault(a => a.Code == editCode);
    }
}

/// <summary>فیلدهای فرم حساب. OriginalCode کد حساب قبل از ویرایش است.</summary>
public sealed class AccountForm
{
    public string? OriginalCode { get; set; }

    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? ParentCode { get; set; }

    public string? AccountType { get; set; }

    public bool IsActive { get; set; } = true;

    public static AccountForm From(AccountInfo account) => new()
    {
        OriginalCode = account.Code,
        Code = account.Code,
        Name = account.Name,
        ParentCode = account.ParentCode,
        AccountType = account.AccountType,
        IsActive = account.IsActive,
    };
}
