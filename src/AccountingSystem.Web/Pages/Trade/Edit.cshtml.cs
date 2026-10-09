using System.Globalization;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Trade;

/// <summary>
/// ویرایش معامله (فقط مدیر). تغییر اطلاعات توصیفی همان سند را به‌روز می‌کند؛
/// تغییر مالی، معامله‌ی قبلی را با سند ابطال باطل می‌کند و نسخه‌ی اصلاحی جایگزین آن می‌شود.
/// </summary>
public class EditModel : PageModel
{
    private readonly CurrencyTradeService _trades;
    private readonly CurrencyAdminService _admin;

    public EditModel(CurrencyTradeService trades, CurrencyAdminService admin)
    {
        _trades = trades;
        _admin = admin;
    }

    [BindProperty]
    public TradeForm Input { get; set; } = new();

    public long Id { get; private set; }

    public bool IsVoided { get; private set; }

    public bool IsAdmin { get; private set; }

    public IReadOnlyList<CurrencyInfo> Currencies { get; private set; } = Array.Empty<CurrencyInfo>();

    public async Task<IActionResult> OnGetAsync(long id, CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        var trade = await _trades.GetTradeAsync(user, id, ct);
        if (trade is null)
        {
            return NotFound();
        }

        Id = trade.Id;
        IsVoided = trade.IsVoided;
        IsAdmin = user.Role == UserRole.Admin;
        Input = new TradeForm
        {
            TradeType = trade.Type == TradeType.Sell ? "SELL" : "BUY",
            BranchId = trade.BranchId,
            CurrencyCode = trade.CurrencyCode,
            Amount = trade.Amount.ToString("0.####", CultureInfo.InvariantCulture),
            Rate = trade.Rate.ToString("0.####", CultureInfo.InvariantCulture),
            Fee = trade.FeeIrr.ToString("0", CultureInfo.InvariantCulture),
            CustomerName = trade.CustomerName,
            NationalCode = trade.NationalCode,
            Note = trade.Note,
            OccurredOn = PersianDate.FormatDate(trade.OccurredAt),
        };
        Currencies = await LoadCurrenciesAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long id, CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        IsAdmin = user.Role == UserRole.Admin;
        Id = id;

        if (!InputParser.TryParseDecimal(Input.Amount, out var amount))
        {
            return await ShowErrorAsync("مقدار ارز را به‌درستی وارد کنید.", id, ct);
        }
        if (!InputParser.TryParseDecimal(Input.Rate, out var rate))
        {
            return await ShowErrorAsync("نرخ را به‌درستی وارد کنید.", id, ct);
        }
        decimal fee = 0m;
        if (!string.IsNullOrWhiteSpace(Input.Fee) && !InputParser.TryParseDecimal(Input.Fee, out fee))
        {
            return await ShowErrorAsync("کارمزد را به‌درستی وارد کنید (عدد ریال).", id, ct);
        }
        DateTime? occurredOn = null;
        if (!string.IsNullOrWhiteSpace(Input.OccurredOn))
        {
            if (!PersianDate.TryParseDate(Input.OccurredOn, out var parsedDate))
            {
                return await ShowErrorAsync("تاریخ معامله را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).", id, ct);
            }
            occurredOn = parsedDate;
        }

        try
        {
            var input = new TradeInput(Input.BranchId, Input.CurrencyCode, amount, rate, Input.CustomerName, Input.NationalCode, Input.Note, fee);
            var type = Input.TradeType == "SELL" ? TradeType.Sell : TradeType.Buy;
            var newId = await _trades.EditTradeAsync(user, id, input, type, occurredOn, DateTime.Now, ct);
            TempData["Success"] = newId is null
                ? $"اطلاعات توصیفی معامله شماره {id} به‌روز شد."
                : $"معامله‌ی شماره {id} باطل شد و نسخه‌ی اصلاحی شماره {newId} جایگزین آن شد.";
            return RedirectToPage("Index");
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            return await ShowErrorAsync(ex.Message, id, ct);
        }
    }

    private async Task<IActionResult> ShowErrorAsync(string message, long id, CancellationToken ct)
    {
        ModelState.AddModelError(string.Empty, message);
        var trade = await _trades.GetTradeAsync(User.ToCurrentUser(), id, ct);
        IsVoided = trade?.IsVoided ?? false;
        Currencies = await LoadCurrenciesAsync(ct);
        return Page();
    }

    private async Task<IReadOnlyList<CurrencyInfo>> LoadCurrenciesAsync(CancellationToken ct) =>
        (await _admin.GetCurrenciesAsync(ct)).Where(c => c.IsActive && c.Code != CurrencyCodes.Irr).ToList();
}
