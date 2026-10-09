using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Customers;

/// <summary>
/// مشتریان مشترک صرافی. همه‌ی کاربران فعال با دسترسی به یک شعبه فهرست را می‌بینند؛
/// ثبت و ویرایش برای مدیر و دارندگان وظیفه‌ی «ثبت معامله» در هر شعبه است.
/// </summary>
public class IndexModel : PageModel
{
    private readonly CustomerService _customers;

    public IndexModel(CustomerService customers)
    {
        _customers = customers;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    /// <summary>شناسه‌ی مشتری در فرم ویرایش؛ خالی یعنی فرم مشتری تازه.</summary>
    [BindProperty(SupportsGet = true)]
    public int? Edit { get; set; }

    [BindProperty]
    public CustomerForm Form { get; set; } = new();

    public IReadOnlyList<CustomerInfo> Customers { get; private set; } = Array.Empty<CustomerInfo>();

    public CustomerInfo? Current { get; private set; }

    public bool CanEdit { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (Current is not null)
        {
            Form = CustomerForm.From(Current);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        var input = new CustomerInput(Form.FullName, Form.NationalCode, Form.Phone, Form.Address, Form.Note);
        try
        {
            if (Form.Id is null)
            {
                var id = await _customers.CreateAsync(user, input, DateTime.Now, ct);
                TempData["Success"] = $"مشتری شماره {id} ثبت شد.";
            }
            else
            {
                await _customers.UpdateAsync(user, Form.Id.Value, input, DateTime.Now, ct);
                TempData["Success"] = "اطلاعات مشتری به‌روز شد.";
            }
            return RedirectToPage(new { search = Search });
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            Edit = Form.Id;
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        CanEdit = await _customers.CanEditAsync(user, ct);
        try
        {
            Customers = await _customers.SearchAsync(user, Search, ct);
            if (Edit is int id)
            {
                Current = await _customers.GetAsync(user, id, ct);
            }
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
    }
}

/// <summary>فیلدهای فرم مشتری. Id خالی یعنی مشتری تازه.</summary>
public sealed class CustomerForm
{
    public int? Id { get; set; }

    public string? FullName { get; set; }

    public string? NationalCode { get; set; }

    public string? Phone { get; set; }

    public string? Address { get; set; }

    public string? Note { get; set; }

    public static CustomerForm From(CustomerInfo customer) => new()
    {
        Id = customer.Id,
        FullName = customer.FullName,
        NationalCode = customer.NationalCode,
        Phone = customer.Phone,
        Address = customer.Address,
        Note = customer.Note,
    };
}
