using System.Globalization;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>
/// موجودی‌های افتتاحیه‌ی ۹۰ روز اخیر. ویرایش و ابطال فقط برای مدیر است؛ هر دو با بازمحاسبه‌ی تاریخچه انجام می‌شوند.
/// </summary>
internal sealed class OpeningsTab : UserControl, IRefreshable
{
    private static readonly string[] Headers =
    {
        "شماره", "تاریخ (شمسی)", "شعبه", "ارز", "مقدار", "نرخ", "بهای ریالی", "ثبت‌کننده", "وضعیت",
    };

    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly Button _refresh = new() { Text = "بروزرسانی", AutoSize = true };
    private readonly Button _edit = new() { Text = "ویرایش موجودی", AutoSize = true };
    private readonly Button _void = new() { Text = "ابطال موجودی", AutoSize = true };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<OpeningInfo> _openings = Array.Empty<OpeningInfo>();

    public OpeningsTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        var tools = UiHelpers.CreateInputPanel();
        tools.Controls.AddRange(new Control[] { _refresh, _edit, _void });
        var isAdmin = user.Role == UserRole.Admin;
        _edit.Visible = isAdmin;
        _void.Visible = isAdmin;

        Controls.Add(_grid);
        Controls.Add(tools);

        _refresh.Click += async (_, _) => await SafeRefreshAsync();
        _edit.Click += async (_, _) => await EditSelectedAsync();
        _void.Click += async (_, _) => await VoidSelectedAsync();
    }

    public async Task RefreshAsync()
    {
        var today = DateTime.Today;
        _openings = await _services.Admin.GetOpeningsAsync(_user, null, today.AddDays(-90), today.AddDays(1));
        var rows = _openings.Select(o => new[]
        {
            o.Id.ToString(CultureInfo.InvariantCulture),
            PersianDate.FormatDate(o.OccurredAt),
            o.BranchName,
            o.CurrencyCode,
            MoneyMath.FormatRate(o.Quantity),
            o.RateIrr is { } rate ? MoneyMath.FormatRate(rate) : "—",
            MoneyMath.FormatAmount(o.CostIrr, 0),
            o.CreatedBy,
            o.IsVoided ? "باطل شد: " + o.VoidReason : "فعال",
        });
        UiHelpers.Fill(_grid, Headers, rows);
    }

    private OpeningInfo? SelectedOpening()
    {
        var index = _grid.CurrentRow?.Index ?? -1;
        return index >= 0 && index < _openings.Count ? _openings[index] : null;
    }

    private async Task EditSelectedAsync()
    {
        try
        {
            var opening = SelectedOpening() ?? throw new BusinessRuleException("یک موجودی افتتاحیه را از جدول انتخاب کنید.");
            if (opening.IsVoided)
            {
                throw new BusinessRuleException("این موجودی افتتاحیه باطل شده است.");
            }

            var quantityText = UiHelpers.PromptText(this, "ویرایش موجودی افتتاحیه",
                $"مقدار جدید (فعلی: {MoneyMath.FormatRate(opening.Quantity)}):");
            if (string.IsNullOrWhiteSpace(quantityText))
            {
                return;
            }
            if (!InputParser.TryParseDecimal(quantityText, out var quantity))
            {
                throw new BusinessRuleException("مقدار را به‌درستی وارد کنید.");
            }

            decimal? rate = null;
            if (opening.CurrencyCode != CurrencyCodes.Irr)
            {
                var rateText = UiHelpers.PromptText(this, "ویرایش موجودی افتتاحیه",
                    $"نرخ جدید هر واحد (فعلی: {MoneyMath.FormatRate(opening.RateIrr ?? 0m)}):");
                if (string.IsNullOrWhiteSpace(rateText))
                {
                    return;
                }
                if (!InputParser.TryParseDecimal(rateText, out var parsedRate))
                {
                    throw new BusinessRuleException("نرخ را به‌درستی وارد کنید.");
                }
                rate = parsedRate;
            }

            var dateText = UiHelpers.PromptText(this, "ویرایش موجودی افتتاحیه",
                "تاریخ جدید (شمسی). خالی بماند تا تاریخ قبلی حفظ شود:");
            DateTime? occurredOn = null;
            if (!string.IsNullOrWhiteSpace(dateText))
            {
                if (!PersianDate.TryParseDate(dateText, out var date))
                {
                    throw new BusinessRuleException("تاریخ را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
                }
                occurredOn = date;
            }

            await _services.Admin.EditOpeningAsync(_user, opening.Id, quantity, rate, occurredOn, DateTime.Now);
            UiHelpers.ShowInfo(this, "موجودی افتتاحیه ویرایش شد؛ نسخه‌ی قبلی باطل و نسخه‌ی اصلاحی ثبت شد.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private async Task VoidSelectedAsync()
    {
        try
        {
            var opening = SelectedOpening() ?? throw new BusinessRuleException("یک موجودی افتتاحیه را از جدول انتخاب کنید.");
            if (opening.IsVoided)
            {
                throw new BusinessRuleException("این موجودی افتتاحیه قبلاً باطل شده است.");
            }

            var reason = UiHelpers.PromptText(this, "ابطال موجودی افتتاحیه",
                $"دلیل ابطال موجودی افتتاحیه‌ی شماره {opening.Id} را وارد کنید:");
            if (string.IsNullOrWhiteSpace(reason))
            {
                return;
            }

            await _services.Admin.VoidOpeningAsync(_user, opening.Id, reason, DateTime.Now);
            UiHelpers.ShowInfo(this, "موجودی افتتاحیه باطل شد و سند ابطال ثبت گردید.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private async Task SafeRefreshAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
