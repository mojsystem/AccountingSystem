using System.Globalization;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Cash;

/// <summary>ویرایش موجودی افتتاحیه (فقط مدیر). نسخه‌ی قبلی باطل و نسخه‌ی اصلاحی ثبت می‌شود.</summary>
public class EditOpeningModel : PageModel
{
    private readonly CurrencyAdminService _admin;

    public EditOpeningModel(CurrencyAdminService admin)
    {
        _admin = admin;
    }

    [BindProperty]
    public OpeningEditForm Input { get; set; } = new();

    public long Id { get; private set; }

    public string CurrencyCode { get; private set; } = string.Empty;

    public string BranchName { get; private set; } = string.Empty;

    public bool IsVoided { get; private set; }

    public bool IsIrr => CurrencyCode == CurrencyCodes.Irr;

    public async Task<IActionResult> OnGetAsync(long id, CancellationToken ct)
    {
        var opening = await _admin.GetOpeningAsync(User.ToCurrentUser(), id, ct);
        if (opening is null)
        {
            return NotFound();
        }

        Id = opening.Id;
        CurrencyCode = opening.CurrencyCode;
        BranchName = opening.BranchName;
        IsVoided = opening.IsVoided;
        Input = new OpeningEditForm
        {
            Quantity = opening.Quantity.ToString("0.####", CultureInfo.InvariantCulture),
            Rate = opening.RateIrr?.ToString("0.####", CultureInfo.InvariantCulture),
            OccurredOn = PersianDate.FormatDate(opening.OccurredAt),
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long id, CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        var old = await _admin.GetOpeningAsync(user, id, ct);
        if (old is null)
        {
            return NotFound();
        }
        Id = id;
        CurrencyCode = old.CurrencyCode;
        BranchName = old.BranchName;
        IsVoided = old.IsVoided;

        try
        {
            if (!InputParser.TryParseDecimal(Input.Quantity, out var quantity))
            {
                throw new BusinessRuleException("مقدار را به‌درستی وارد کنید.");
            }

            decimal? rate = null;
            if (!IsIrr)
            {
                if (!InputParser.TryParseDecimal(Input.Rate, out var parsedRate))
                {
                    throw new BusinessRuleException("نرخ را به‌درستی وارد کنید.");
                }
                rate = parsedRate;
            }

            DateTime? occurredOn = null;
            if (!string.IsNullOrWhiteSpace(Input.OccurredOn))
            {
                if (!PersianDate.TryParseDate(Input.OccurredOn, out var date))
                {
                    throw new BusinessRuleException("تاریخ را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
                }
                occurredOn = date;
            }

            var newId = await _admin.EditOpeningAsync(user, id, quantity, rate, occurredOn, DateTime.Now, ct);
            TempData["Success"] = $"موجودی افتتاحیه‌ی شماره {id} باطل شد و نسخه‌ی اصلاحی شماره {newId} ثبت گردید.";
            return RedirectToPage("Index");
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }
}

public sealed class OpeningEditForm
{
    public string? Quantity { get; set; }

    public string? Rate { get; set; }

    public string? OccurredOn { get; set; }
}
