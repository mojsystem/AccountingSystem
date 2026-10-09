using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;

namespace AccountingSystem.WinForms.Tabs;

internal sealed class TradeTab : UserControl, IRefreshable
{
    private static readonly string[] GridHeaders =
    {
        "شماره", "شعبه", "زمان (شمسی)", "نوع", "ارز", "مقدار", "نرخ", "مبلغ ریالی", "کارمزد", "سود (ریال)", "مشتری", "ثبت‌کننده", "وضعیت",
    };

    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly RadioButton _buy = new() { Text = "خرید از مشتری (پرداخت ریال)", Checked = true, AutoSize = true };
    private readonly RadioButton _sell = new() { Text = "فروش به مشتری (دریافت ریال)", AutoSize = true };
    private readonly ComboBox _branch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ComboBox _currency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly TextBox _amount = new() { Width = 110 };
    private readonly TextBox _rate = new() { Width = 110 };
    private readonly TextBox _fee = new() { Width = 100, Text = "0" };
    private readonly TextBox _customer = new() { Width = 150 };
    private readonly TextBox _nationalCode = new() { Width = 120 };
    private readonly TextBox _note = new() { Width = 170 };
    private readonly Label _preview = UiHelpers.MakeLabel(string.Empty);
    private readonly Button _submit = new() { Text = "ثبت معامله", AutoSize = true };

    private readonly TextBox _from = new() { Width = 110, PlaceholderText = "۱۴۰۵/۰۷/۰۱" };
    private readonly TextBox _to = new() { Width = 110, PlaceholderText = "۱۴۰۵/۰۷/۳۰" };
    private readonly ComboBox _filterBranch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly Button _show = new() { Text = "نمایش", AutoSize = true };
    private readonly Button _receipt = new() { Text = "رسید", AutoSize = true };
    private readonly Button _void = new() { Text = "ابطال معامله", AutoSize = true };
    private readonly Button _excel = new() { Text = "خروجی Excel", AutoSize = true };

    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<RateInfo> _rates = Array.Empty<RateInfo>();
    private IReadOnlyList<BranchInfo> _branches = Array.Empty<BranchInfo>();
    private IReadOnlyList<TradeInfo> _trades = Array.Empty<TradeInfo>();
    private bool _filling;

    public TradeTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        var inputs = UiHelpers.CreateInputPanel();
        inputs.Controls.AddRange(new Control[]
        {
            _buy,
            _sell,
            UiHelpers.MakeLabel("شعبه:"), _branch,
            UiHelpers.MakeLabel("ارز:"), _currency,
            UiHelpers.MakeLabel("مقدار:"), _amount,
            UiHelpers.MakeLabel("نرخ (ریال):"), _rate,
            UiHelpers.MakeLabel("کارمزد (ریال):"), _fee,
            UiHelpers.MakeLabel("نام مشتری:"), _customer,
            UiHelpers.MakeLabel("کد ملی:"), _nationalCode,
            UiHelpers.MakeLabel("توضیحات:"), _note,
            _preview,
            _submit,
        });

        var filters = UiHelpers.CreateInputPanel();
        filters.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("از تاریخ (شمسی):"), _from,
            UiHelpers.MakeLabel("تا تاریخ (شمسی):"), _to,
            UiHelpers.MakeLabel("شعبه:"), _filterBranch,
            _show, _receipt, _void, _excel,
        });
        _void.Visible = user.Role == UserRole.Admin;

        Controls.Add(_grid);
        Controls.Add(filters);
        Controls.Add(inputs);

        _buy.CheckedChanged += (_, _) => FillRateFromSelection();
        _sell.CheckedChanged += (_, _) => FillRateFromSelection();
        _currency.SelectedIndexChanged += (_, _) => FillRateFromSelection();
        _branch.SelectedIndexChanged += (_, _) => FillRateFromSelection();
        _amount.TextChanged += (_, _) => UpdatePreview();
        _rate.TextChanged += (_, _) => UpdatePreview();
        _fee.TextChanged += (_, _) => UpdatePreview();
        _submit.Click += async (_, _) => await SubmitAsync();
        _show.Click += async (_, _) => await SafeRefreshAsync();
        _receipt.Click += async (_, _) => await OpenReceiptAsync();
        _void.Click += async (_, _) => await VoidSelectedAsync();
        _excel.Click += (_, _) => ExportExcel();
    }

    public async Task RefreshAsync()
    {
        var currencies = await _services.Admin.GetCurrenciesAsync();
        _rates = await _services.Admin.GetLatestRatesAsync(_user, null);
        _branches = await _services.Branches.GetBranchesAsync();

        _filling = true;
        try
        {
            var previousCurrency = (_currency.SelectedItem as ComboItem)?.Value;
            _currency.Items.Clear();
            foreach (var c in currencies.Where(x => x.IsActive && x.Code != CurrencyCodes.Irr))
            {
                _currency.Items.Add(new ComboItem(c.Code, $"{c.Code} - {c.Name}"));
            }
            UiHelpers.SelectByValue(_currency, previousCurrency);

            var previousBranch = (_branch.SelectedItem as ComboItem)?.Value;
            UiHelpers.FillBranches(_branch, _branches, _user, includeAll: false, selectedValue: previousBranch);

            var previousFilter = (_filterBranch.SelectedItem as ComboItem)?.Value;
            UiHelpers.FillBranches(_filterBranch, _branches, _user, includeAll: true, selectedValue: previousFilter);
        }
        finally
        {
            _filling = false;
        }

        FillRateFromSelection();
        UpdatePreview();
        await LoadTradesAsync();
    }

    private async Task LoadTradesAsync()
    {
        var (from, to) = UiHelpers.ParseJalaliRange(_from, _to);
        _trades = await _services.Reports.GetTradesAsync(_user, UiHelpers.SelectedBranchId(_filterBranch), from, to);

        var rows = _trades.Select(t => new[]
        {
            t.Id.ToString(),
            t.BranchCode,
            PersianDate.FormatDateTime(t.OccurredAt),
            t.Type == TradeType.Buy ? "خرید از مشتری" : "فروش به مشتری",
            t.CurrencyCode,
            MoneyMath.FormatRate(t.Amount),
            MoneyMath.FormatRate(t.Rate),
            MoneyMath.FormatAmount(t.IrrAmount, 0),
            MoneyMath.FormatAmount(t.FeeIrr, 0),
            MoneyMath.FormatAmount(t.ProfitIrr, 0),
            t.CustomerName ?? string.Empty,
            t.CreatedBy,
            t.IsVoided ? "باطل شد" : "فعال",
        });
        UiHelpers.Fill(_grid, GridHeaders, rows);
    }

    private TradeInfo? SelectedTrade()
    {
        var index = _grid.CurrentRow?.Index ?? -1;
        return index >= 0 && index < _trades.Count ? _trades[index] : null;
    }

    private void FillRateFromSelection()
    {
        if (_filling || _currency.SelectedItem is not ComboItem item || UiHelpers.SelectedBranchId(_branch) is not { } branchId)
        {
            return;
        }
        var rate = _rates.FirstOrDefault(r => r.BranchId == branchId && r.CurrencyCode == item.Value);
        if (rate is null)
        {
            return;
        }
        var value = _sell.Checked ? rate.SellRateIrr : rate.BuyRateIrr;
        _rate.Text = MoneyMath.FormatRate(value);
    }

    private void UpdatePreview()
    {
        if (!InputParser.TryParseDecimal(_amount.Text, out var amount) || !InputParser.TryParseDecimal(_rate.Text, out var rate))
        {
            _preview.Text = string.Empty;
            return;
        }

        var irr = MoneyMath.RoundIrr(amount * rate);
        var fee = InputParser.TryParseDecimal(_fee.Text, out var parsedFee) ? parsedFee : 0m;
        var cash = _sell.Checked ? irr + fee : irr - fee;
        var cashLabel = _sell.Checked ? "دریافتی از مشتری" : "پرداختی به مشتری";
        _preview.Text = $"مبلغ ریالی: {MoneyMath.FormatAmount(irr, 0)} ریال   |   {cashLabel}: {MoneyMath.FormatAmount(cash, 0)} ریال";
    }

    private async Task SubmitAsync()
    {
        try
        {
            if (_currency.SelectedItem is not ComboItem item)
            {
                throw new BusinessRuleException("ارز را انتخاب کنید.");
            }
            var branchId = UiHelpers.RequiredBranchId(_branch);
            if (!InputParser.TryParseDecimal(_amount.Text, out var amount))
            {
                throw new BusinessRuleException("مقدار ارز را به‌درستی وارد کنید.");
            }
            if (!InputParser.TryParseDecimal(_rate.Text, out var rate))
            {
                throw new BusinessRuleException("نرخ را به‌درستی وارد کنید.");
            }
            var fee = 0m;
            if (!string.IsNullOrWhiteSpace(_fee.Text) && !InputParser.TryParseDecimal(_fee.Text, out fee))
            {
                throw new BusinessRuleException("کارمزد را به‌درستی وارد کنید (عدد ریال).");
            }

            var input = new TradeInput(branchId, item.Value, amount, rate, _customer.Text, _nationalCode.Text, _note.Text, fee);
            var id = _sell.Checked
                ? await _services.Trades.SellToCustomerAsync(input, _user, DateTime.Now)
                : await _services.Trades.BuyFromCustomerAsync(input, _user, DateTime.Now);

            _amount.Clear();
            _fee.Text = "0";
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

    private async Task OpenReceiptAsync()
    {
        try
        {
            var trade = SelectedTrade() ?? throw new BusinessRuleException("یک معامله از فهرست انتخاب کنید.");
            var html = await _services.Receipts.RenderTradeReceiptAsync(_user, trade.Id);
            UiHelpers.OpenHtml(html, $"receipt-{trade.Id}.html");
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
            var trade = SelectedTrade() ?? throw new BusinessRuleException("یک معامله از فهرست انتخاب کنید.");
            if (trade.IsVoided)
            {
                throw new BusinessRuleException("این معامله قبلاً باطل شده است.");
            }

            var reason = UiHelpers.PromptText(this, "ابطال معامله", $"دلیل ابطال معامله شماره {trade.Id} را وارد کنید:");
            if (string.IsNullOrWhiteSpace(reason))
            {
                return;
            }

            var answer = MessageBox.Show(this,
                "با ابطال، سند معکوس ثبت می‌شود و موجودی صندوق و بهای ارز به حالت قبل برمی‌گردد. ادامه می‌دهید؟",
                "تأیید ابطال",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            await _services.Trades.VoidTradeAsync(_user, trade.Id, reason, DateTime.Now);
            UiHelpers.ShowInfo(this, $"معامله شماره {trade.Id} باطل شد و سند ابطال ثبت گردید.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private void ExportExcel()
    {
        try
        {
            var (from, to) = UiHelpers.ParseJalaliRange(_from, _to);
            var bytes = WorkbookBuilder.Trades(_trades);
            UiHelpers.SaveExcel(this, bytes, $"trades-{from:yyyyMMdd}-{to.AddDays(-1):yyyyMMdd}.xlsx");
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
            await LoadTradesAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
