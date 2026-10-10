using System.Net;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Trade;

/// <summary>رسید HTML معامله. صفحه‌ی خروجی مستقل است و از دکمه‌ی «چاپ» مرورگر استفاده می‌کند.</summary>
public class ReceiptModel : PageModel
{
    private readonly ReceiptService _receipts;

    public ReceiptModel(ReceiptService receipts)
    {
        _receipts = receipts;
    }

    public async Task<IActionResult> OnGetAsync(long id, CancellationToken ct)
    {
        try
        {
            var html = await _receipts.RenderTradeReceiptAsync(User.ToCurrentUser(), id, ct);
            return Content(html, "text/html; charset=utf-8");
        }
        catch (BusinessRuleException ex)
        {
            var message = WebUtility.HtmlEncode(ex.Message);
            return new ContentResult
            {
                Content = $"<!DOCTYPE html><html lang=\"fa\" dir=\"rtl\"><meta charset=\"utf-8\"><body style=\"font-family:Tahoma\"><p>{message}</p></body></html>",
                ContentType = "text/html; charset=utf-8",
                StatusCode = StatusCodes.Status404NotFound,
            };
        }
    }
}
