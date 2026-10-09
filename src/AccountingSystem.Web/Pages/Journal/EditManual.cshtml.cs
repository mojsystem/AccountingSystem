using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Journal;

/// <summary>
/// ویرایش سند دستی (فقط مدیر). ویرایش با ابطال سند قبلی و ثبت نسخه‌ی اصلاحی انجام می‌شود.
/// </summary>
public class EditManualModel : PageModel
{
    private readonly ManualJournalService _manual;

    public EditManualModel(ManualJournalService manual)
    {
        _manual = manual;
    }

    [BindProperty]
    public ManualForm Input { get; set; } = new();

    public long Id { get; private set; }

    public int BranchId { get; private set; }

    public string BranchName { get; private set; } = string.Empty;

    public bool IsVoided { get; private set; }

    public IReadOnlyList<AccountInfo> Accounts { get; private set; } = Array.Empty<AccountInfo>();

    public async Task<IActionResult> OnGetAsync(long id, CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        var entry = await _manual.GetAsync(user, id, ct);
        if (entry is null || entry.SourceType != SourceTypes.Manual)
        {
            return NotFound();
        }

        Id = entry.Id;
        BranchId = entry.BranchId;
        BranchName = entry.BranchName;
        IsVoided = entry.IsVoided;
        Input = new ManualForm
        {
            BranchId = entry.BranchId,
            Description = entry.Description,
            OccurredOn = PersianDate.FormatDate(entry.OccurredAt),
            Lines = entry.Lines
                .Select(l => new ManualLineForm
                {
                    AccountCode = l.AccountCode,
                    Debit = l.Debit == 0m ? null : l.Debit.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                    Credit = l.Credit == 0m ? null : l.Credit.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                })
                .Concat(Enumerable.Repeat(new ManualLineForm(), 6))
                .Take(Math.Max(6, entry.Lines.Count))
                .ToList(),
        };
        Accounts = await _manual.GetAccountsAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long id, CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        Id = id;
        try
        {
            var lines = IndexModel.ParseLines(Input.Lines);
            var occurredOn = IndexModel.ParseDate(Input.OccurredOn);
            var newId = await _manual.EditAsync(user, id, Input.Description ?? string.Empty, occurredOn, lines, DateTime.Now, ct);
            TempData["Success"] = $"سند دستی شماره {id} باطل شد و نسخه‌ی اصلاحی شماره {newId} ثبت گردید.";
            return RedirectToPage("Index");
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            Accounts = await _manual.GetAccountsAsync(ct);
            var entry = await _manual.GetAsync(user, id, ct);
            IsVoided = entry?.IsVoided ?? false;
            BranchId = entry?.BranchId ?? 0;
            BranchName = entry?.BranchName ?? string.Empty;
            return Page();
        }
    }
}
