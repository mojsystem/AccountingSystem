using System.Globalization;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.BankAccounts;

/// <summary>ایجاد حساب بانکی نام‌دار به تفکیک شعبه و ارز (فقط مدیر سیستم).</summary>
public sealed class IndexModel : PageModel
{
    private readonly BankAccountService _bankAccounts;
    private readonly BranchService _branches;
    private readonly CurrencyAdminService _currencies;

    public IndexModel(BankAccountService bankAccounts, BranchService branches, CurrencyAdminService currencies)
    {
        _bankAccounts = bankAccounts;
        _branches = branches;
        _currencies = currencies;
    }

    [BindProperty]
    public BankAccountForm Input { get; set; } = new();

    public IReadOnlyList<BankAccountInfo> Accounts { get; private set; } = Array.Empty<BankAccountInfo>();
    public IReadOnlyList<BranchInfo> BranchList { get; private set; } = Array.Empty<BranchInfo>();
    public IReadOnlyList<CurrencyInfo> CurrencyList { get; private set; } = Array.Empty<CurrencyInfo>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            if (!InputParser.TryParseDecimal(Input.OpeningBalance, out var balance))
            {
                throw new BusinessRuleException("موجودی افتتاحیه را به‌درستی وارد کنید.");
            }
            decimal? openingRate = null;
            if (!string.IsNullOrWhiteSpace(Input.OpeningRateIrr))
            {
                if (!InputParser.TryParseDecimal(Input.OpeningRateIrr, out var parsedRate))
                {
                    throw new BusinessRuleException("نرخ ریالی افتتاحیه را به‌درستی وارد کنید.");
                }
                openingRate = parsedRate;
            }

            var id = await _bankAccounts.CreateAsync(User.ToCurrentUser(), Input.BranchId, Input.Name,
                Input.CurrencyCode, balance, openingRate, DateTime.Now, ct);
            TempData["Success"] = $"حساب بانکی شماره {id} ثبت شد؛ موجودی افتتاحیه و سند حسابداری آن ثبت شده‌اند.";
            return RedirectToPage();
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Accounts = await _bankAccounts.GetBankAccountsAsync(User.ToCurrentUser(), null, ct);
        BranchList = await _branches.GetBranchesAsync(ct);
        CurrencyList = (await _currencies.GetCurrenciesAsync(ct)).Where(currency => currency.IsActive).ToList();
        if (Input.BranchId == 0)
        {
            Input.BranchId = BranchList.FirstOrDefault()?.Id ?? 0;
        }
        if (string.IsNullOrWhiteSpace(Input.CurrencyCode))
        {
            Input.CurrencyCode = CurrencyCodes.Irr;
        }
    }
}

public sealed class BankAccountForm
{
    public int BranchId { get; set; }
    public string? Name { get; set; }
    public string? CurrencyCode { get; set; }
    public string? OpeningBalance { get; set; }
    public string? OpeningRateIrr { get; set; }
}
