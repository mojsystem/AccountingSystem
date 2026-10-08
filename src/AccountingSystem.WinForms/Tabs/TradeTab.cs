using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

internal sealed class TradeTab : UserControl, IRefreshable
{
    private static readonly string[] GridHeaders =
    {
        "شماره", "زمان (شمسی)", "نوع", "ارز", "مقدار", "نرخ", "مبلغ ریالی", "سود (ریال)", "مشتری", "ثبت‌کننده",
    };

    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly RadioButton _buy = new() { Text = "خرید از مشتری (پرداخت ریال)", Checked = true, AutoSize = true };
    private readonly RadioButton _sell = new() { Text = "فروش به مشتری (دریافت ریال)", AutoSize = true };
    private readonly ComboBox _currency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly TextBox _amount = new() { Width = 110 };
    private readonly TextBox _rate = new() { Width = 110 };
    private readonly TextBox _customer = new() { Width = 170 };
    private readonly TextBox _nationalCode = new() { Width = 120 };
    private readonly TextBox _note = new() { Width = 200 };
    private readonly Label _preview = UiHelpers.MakeLabel(string.Empty);
    private readonly Button _submit = new() { Text = "ثبت معامله", AutoSize = true };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<RateInfo> _rates = Array.Empty<RateInfo>();

    public TradeTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        var inputs = UiHelpers.CreateInputPanel();
        inputs.Controls.AddRange(new Control[]
        {
            _buy,
            _sell,
            UiHelpers.MakeLabel("ارز:"), _currency,
            UiHelpers.MakeLabel("مقدار:"), _amount,
            UiHelpers.MakeLabel("نرخ (ریال):"), _rate,
            UiHelpers.MakeLabel("نام مشتری:"), _customer,
            UiHelpers.MakeLabel("کد ملی:"), _nationalCode,
            UiHelpers.MakeLabel("توضیحات:"), _note,
            _preview,
            _submit,
        });

        Controls.Add(_grid);
        Controls.Add(inputs);

        _buy.CheckedChanged += (_, _) => FillRateFromSelection();
        _currency.SelectedIndexChanged += (_, _) => FillRateFromSelection();
        _amount.TextChanged += (_, _) => UpdatePreview();
        _rate.TextChanged += (_, _) => UpdatePreview();
        _submit.Click += async (_, _) => await SubmitAsync();
    }

    public async Task RefreshAsync()
    {
        var currencies = await _services.Admin.GetCurrenciesAsync();
        _rates = await _services.Admin.GetLatestRatesAsync();
        var today = DateTime.Now.Date;
        var trades = await _services.Reports.GetTradesAsync(today, today.AddDays(1));

        var previous = (_currency.SelectedItem as ComboItem)?.Value;
        _currency.Items.Clear();
        foreach (var c in currencies.Where(x => x.IsActive && x.Code != CurrencyCodes.Irr))
        {
            _currency.Items.Add(new ComboItem(c.Code, $"{c.Code} - {c.Name}"));
        }
        UiHelpers.SelectByValue(_currency, previous);
        FillRateFromSelection();
        UpdatePreview();

        var rows = trades.Select(t => new[]
        {
            t.Id.ToString(),
            PersianDate.FormatDateTime(t.OccurredAt),
            t.Type == TradeType.Buy ? "خرید از مشتری" : "فروش به مشتری",
            t.CurrencyCode,
            MoneyMath.FormatRate(t.Amount),
            MoneyMath.FormatRate(t.Rate),
            MoneyMath.FormatAmount(t.IrrAmount, 0),
            MoneyMath.FormatAmount(t.ProfitIrr, 0),
            t.CustomerName ?? string.Empty,
            t.CreatedBy,
        });
        UiHelpers.Fill(_grid, GridHeaders, rows);
    }

    private void FillRateFromSelection()
    {
        if (_currency.SelectedItem is not ComboItem item)
        {
            return;
        }
        var rate = _rates.FirstOrDefault(r => r.CurrencyCode == item.Value);
        if (rate is null)
        {
            return;
        }
        var value = _sell.Checked ? rate.SellRateIrr : rate.BuyRateIrr;
        _rate.Text = MoneyMath.FormatRate(value);
    }

    private void UpdatePreview()
    {
        if (InputParser.TryParseDecimal(_amount.Text, out var amount) && InputParser.TryParseDecimal(_rate.Text, out var rate))
        {
            _preview.Text = "مبلغ ریالی: " + MoneyMath.FormatAmount(MoneyMath.RoundIrr(amount * rate), 0) + " ریال";
        }
        else
        {
            _preview.Text = string.Empty;
        }
    }

    private async Task SubmitAsync()
    {
        try
        {
            if (_currency.SelectedItem is not ComboItem item)
            {
                throw new BusinessRuleException("ارز را انتخاب کنید.");
            }
            if (!InputParser.TryParseDecimal(_amount.Text, out var amount))
            {
                throw new BusinessRuleException("مقدار ارز را به‌درستی وارد کنید.");
            }
            if (!InputParser.TryParseDecimal(_rate.Text, out var rate))
            {
                throw new BusinessRuleException("نرخ را به‌درستی وارد کنید.");
            }

            var input = new TradeInput(item.Value, amount, rate, _customer.Text, _nationalCode.Text, _note.Text);
            var id = _sell.Checked
                ? await _services.Trades.SellToCustomerAsync(input, _user, DateTime.Now)
                : await _services.Trades.BuyFromCustomerAsync(input, _user, DateTime.Now);

            _amount.Clear();
            _customer.Clear();
            _nationalCode.Clear();
            _note.Clear();
            UiHelpers.ShowInfo(this, $"معامله شماره {id} ثبت شد.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
