using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

internal sealed class CashTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly TextBox _irrAmount = new() { Width = 160 };
    private readonly Button _irrButton = new() { Text = "ثبت موجودی افتتاحیه ریال", AutoSize = true };
    private readonly ComboBox _fxCurrency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly TextBox _fxQuantity = new() { Width = 120 };
    private readonly TextBox _fxRate = new() { Width = 120 };
    private readonly Button _fxButton = new() { Text = "ثبت موجودی افتتاحیه ارز", AutoSize = true };
    private readonly FlowLayoutPanel _openingPanel = UiHelpers.CreateInputPanel();
    private readonly DataGridView _grid = UiHelpers.CreateGrid();

    public CashTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        _openingPanel.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("موجودی افتتاحیه ریال (ریال):"), _irrAmount, _irrButton,
            UiHelpers.MakeLabel("ارز:"), _fxCurrency,
            UiHelpers.MakeLabel("مقدار:"), _fxQuantity,
            UiHelpers.MakeLabel("نرخ خرید واحد (ریال):"), _fxRate,
            _fxButton,
        });
        _openingPanel.Visible = user.Role == UserRole.Admin;

        Controls.Add(_grid);
        Controls.Add(_openingPanel);

        _irrButton.Click += async (_, _) => await OpeningIrrAsync();
        _fxButton.Click += async (_, _) => await OpeningFxAsync();
    }

    public async Task RefreshAsync()
    {
        var boxes = await _services.Admin.GetCashBoxesAsync();
        var currencies = await _services.Admin.GetCurrenciesAsync();

        var previous = (_fxCurrency.SelectedItem as ComboItem)?.Value;
        _fxCurrency.Items.Clear();
        foreach (var c in currencies.Where(x => x.IsActive && x.Code != CurrencyCodes.Irr))
        {
            _fxCurrency.Items.Add(new ComboItem(c.Code, $"{c.Code} - {c.Name}"));
        }
        UiHelpers.SelectByValue(_fxCurrency, previous);

        var rows = boxes.Select(b => new[]
        {
            b.Name,
            b.CurrencyCode,
            b.CurrencyCode == CurrencyCodes.Irr ? MoneyMath.FormatAmount(b.Balance, 0) : MoneyMath.FormatRate(b.Balance),
            PersianDate.FormatDateTime(b.UpdatedAt),
        });
        UiHelpers.Fill(_grid, new[] { "صندوق", "ارز", "موجودی", "آخرین تغییر (شمسی)" }, rows);
    }

    private async Task OpeningIrrAsync()
    {
        try
        {
            if (!InputParser.TryParseDecimal(_irrAmount.Text, out var amount))
            {
                throw new BusinessRuleException("مبلغ را به‌درستی وارد کنید.");
            }

            await _services.Admin.OpeningIrrAsync(_user, amount, DateTime.Now);
            _irrAmount.Clear();
            UiHelpers.ShowInfo(this, "موجودی افتتاحیه ریال ثبت شد.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private async Task OpeningFxAsync()
    {
        try
        {
            if (_fxCurrency.SelectedItem is not ComboItem item)
            {
                throw new BusinessRuleException("ارز را انتخاب کنید.");
            }
            if (!InputParser.TryParseDecimal(_fxQuantity.Text, out var quantity) || !InputParser.TryParseDecimal(_fxRate.Text, out var rate))
            {
                throw new BusinessRuleException("مقدار و نرخ را به‌درستی وارد کنید.");
            }

            await _services.Admin.OpeningForeignAsync(_user, item.Value, quantity, rate, DateTime.Now);
            _fxQuantity.Clear();
            _fxRate.Clear();
            UiHelpers.ShowInfo(this, "موجودی افتتاحیه ارز ثبت شد.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
