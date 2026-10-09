using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Branches;

/// <summary>مدیریت شعبه‌ها (فقط مدیر؛ دسترسی پوشه در Program.cs تنظیم شده است).</summary>
public class IndexModel : PageModel
{
    private readonly BranchService _branches;

    public IndexModel(BranchService branches)
    {
        _branches = branches;
    }

    [BindProperty]
    public BranchForm Input { get; set; } = new();

    public IReadOnlyList<BranchInfo> BranchList { get; private set; } = Array.Empty<BranchInfo>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        BranchList = await _branches.GetBranchesAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            await _branches.CreateBranchAsync(User.ToCurrentUser(), Input.Code ?? string.Empty, Input.Name ?? string.Empty, DateTime.Now, ct);
            TempData["Success"] = "شعبه‌ی جدید ثبت شد. صندوق‌ها و موجودی ارزها برای آن ساخته شدند.";
            return RedirectToPage();
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            BranchList = await _branches.GetBranchesAsync(ct);
            return Page();
        }
    }
}

public sealed class BranchForm
{
    public string? Code { get; set; }

    public string? Name { get; set; }
}
