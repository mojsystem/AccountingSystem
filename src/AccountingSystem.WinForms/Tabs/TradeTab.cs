using System.Globalization;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;

namespace AccountingSystem.WinForms.Tabs;

internal sealed class TradeTab : UserControl, IRefreshable
{
    private static readonly string[] GridHeaders =
    {
        "شماره", "شعبه", "زمان (شمسی)", "نوع", "ارز", "مقدار", "نرخ", "مبلغ ریالی", "روش تسویه", "دریافت/پرداخت", "کارمزد", "سود (ریال)", "مشتری", "ثبت‌کننده", "وضعیت",
    };

    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly RadioButton _buy = new() { Text = "خرید از مشتری (پرداخت ریال)", Checked = true, AutoSize = true };
    private readonly RadioButton _sell = new() { Text = "فروش به مشتری (دریافت ریال)", AutoSize = true };
    private readonly ComboBox _branch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ComboBox _currency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly TextBox _amount = new() { Width = 110 };
    private readonly TextBox _rate = new() { Width = 110 };
    private readonly ComboBox _settlementMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly ComboBox _settlementCurrency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _rateMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly TextBox _crossRate = new() { Width = 120 };
    private readonly CheckBox _applyOffset = new() { Text = "تهاتر مانده‌ی مشتری", AutoSize = true };
    private readonly Label _settlementCurrencyLabel = UiHelpers.MakeLabel("ارز مقابل:");
    private readonly Label _rateModeLabel = UiHelpers.MakeLabel("روش نرخ:");
    private readonly Label _crossRateLabel = UiHelpers.MakeLabel("نرخ جفت‌ارز:");
    private readonly Label _settlementsLabel = UiHelpers.MakeLabel("ریز دریافت/پرداخت:");
    private readonly DataGridView _settlementsGrid = new()
    {
        Width = 440,
        Height = 118,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        BackgroundColor = SystemColors.Window,
        EditMode = DataGridViewEditMode.EditOnEnter,
    };
    private readonly TextBox _fee = new() { Width = 100, Text = "0" };
    private readonly ComboBox _customerBox = new()
    {
        Width = 260,
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems,
    };
    private readonly Button _newCustomer = new() { Text = "مشتری تازه…", AutoSize = true };
    private readonly TextBox _note = new() { Width = 170 };
    private readonly TextBox _occurredOn = new() { Width = 110, PlaceholderText = "امروز" };
    private readonly Label _preview = UiHelpers.MakeLabel(string.Empty);
    private readonly Button _submit = new() { Text = "ثبت معامله", AutoSize = true };

    private readonly TextBox _from = new() { Width = 110, PlaceholderText = "۱۴۰۵/۰۷/۰۱" };
    private readonly TextBox _to = new() { Width = 110, PlaceholderText = "۱۴۰۵/۰۷/۳۰" };
    private readonly ComboBox _filterBranch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly Button _show = new() { Text = "نمایش", AutoSize = true };
    private readonly Button _receipt = new() { Text = "رسید", AutoSize = true };
    private readonly Button _void = new() { Text = "ابطال معامله", AutoSize = true };
    private readonly Button _edit = new() { Text = "ویرایش معامله", AutoSize = true };
    private readonly Button _cancelEdit = new() { Text = "انصراف از ویرایش", AutoSize = true, Visible = false };
    private long? _editingTradeId;
    private readonly Button _excel = new() { Text = "خروجی Excel", AutoSize = true };

    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<RateInfo> _rates = Array.Empty<RateInfo>();
    private IReadOnlyList<CurrencyInfo> _currencies = Array.Empty<CurrencyInfo>();
    private IReadOnlyList<BranchInfo> _branches = Array.Empty<BranchInfo>();
    private IReadOnlyList<BranchInfo> _readableBranches = Array.Empty<BranchInfo>();
    private IReadOnlyList<TradeInfo> _trades = Array.Empty<TradeInfo>();
    private bool _filling;

    public TradeTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;
        _settlementMode.Items.AddRange(new object[]
        {
            new ComboItem("DIRECT", "تبادل مستقیم دو ارز"),
            new ComboItem("SPLIT", "چندبخشی"),
            new ComboItem("ACCOUNT", "حساب مشتری"),
        });
        _rateMode.Items.AddRange(new object[]
        {
            new ComboItem("DERIVED", "مشتق از نرخ‌های روز"),
            new ComboItem("DIRECT", "نرخ مستقیم جفت‌ارز"),
        });
        _settlementMode.SelectedIndex = 0;
        _rateMode.SelectedIndex = 0;

        var inputs = UiHelpers.CreateInputPanel();
        inputs.Controls.AddRange(new Control[]
        {
            _buy,
            _sell,
            UiHelpers.MakeLabel("شعبه:"), _branch,
            UiHelpers.MakeLabel("ارز:"), _currency,
            UiHelpers.MakeLabel("مقدار:"), _amount,
            UiHelpers.MakeLabel("نرخ پایه (ریال):"), _rate,
            UiHelpers.MakeLabel("روش تسویه:"), _settlementMode,
            _settlementCurrencyLabel, _settlementCurrency,
            _rateModeLabel, _rateMode,
            _crossRateLabel, _crossRate,
            _applyOffset,
            _settlementsLabel, _settlementsGrid,
            UiHelpers.MakeLabel("کارمزد (ریال):"), _fee,
            UiHelpers.MakeLabel("مشتری:"), _customerBox, _newCustomer,
            UiHelpers.MakeLabel("توضیحات:"), _note,
            UiHelpers.MakeLabel("تاریخ (شمسی):"), _occurredOn,
            _preview,
            _submit,
            _cancelEdit,
        });

        var filters = UiHelpers.CreateInputPanel();
        filters.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("از تاریخ (شمسی):"), _from,
            UiHelpers.MakeLabel("تا تاریخ (شمسی):"), _to,
            UiHelpers.MakeLabel("شعبه:"), _filterBranch,
            _show, _receipt, _void, _edit, _excel,
        });
        // دسترسی‌ها در RefreshAsync از دیتابیس خوانده می‌شوند.
        _void.Visible = false;
        _edit.Visible = false;

        Controls.Add(_grid);
        Controls.Add(filters);
        Controls.Add(inputs);

        _buy.CheckedChanged += (_, _) => FillRateFromSelection();
        _sell.CheckedChanged += (_, _) => FillRateFromSelection();
        _currency.SelectedIndexChanged += (_, _) => FillRateFromSelection();
        _branch.SelectedIndexChanged += (_, _) => FillRateFromSelection();
        _settlementCurrency.SelectedIndexChanged += (_, _) => FillRateFromSelection();
        _settlementMode.SelectedIndexChanged += (_, _) => UpdateSettlementControls(updateRate: true);
        _rateMode.SelectedIndexChanged += (_, _) => UpdateSettlementControls(updateRate: true);
        _amount.TextChanged += (_, _) => UpdatePreview();
        _rate.TextChanged += (_, _) => UpdatePreview();
        _crossRate.TextChanged += (_, _) => UpdatePreview();
        _fee.TextChanged += (_, _) => UpdatePreview();
        _submit.Click += async (_, _) => await SubmitAsync();
        _newCustomer.Click += async (_, _) => await AddCustomerAsync();
        _show.Click += async (_, _) => await SafeRefreshAsync();
        _receipt.Click += async (_, _) => await OpenReceiptAsync();
        _void.Click += async (_, _) => await VoidSelectedAsync();
        _edit.Click += (_, _) => StartEdit();
        _cancelEdit.Click += (_, _) => CancelEdit();
        _excel.Click += (_, _) => ExportExcel();
        UpdateSettlementControls();
    }

    public async Task RefreshAsync()
    {
        _edit.Visible = await _services.Permissions.HasAnyAsync(_user, Permission.TradeEdit);
        _void.Visible = await _services.Permissions.HasAnyAsync(_user, Permission.TradeVoid);

        await LoadCustomersAsync();
        _newCustomer.Enabled = await _services.Customers.CanEditAsync(_user);
        var currencies = await _services.Admin.GetCurrenciesAsync();
        _currencies = currencies.Where(c => c.IsActive).ToList();
        _branches = await _services.Permissions.GetBranchesAsync(_user, Permission.TradeRecord);
        _readableBranches = await _services.Permissions.GetBranchesAsync(_user);
        var rateList = new List<RateInfo>();
        foreach (var branch in _branches)
        {
            rateList.AddRange(await _services.Admin.GetLatestRatesAsync(_user, branch.Id));
        }
        _rates = rateList;

        _filling = true;
        try
        {
            var previousCurrency = (_currency.SelectedItem as ComboItem)?.Value;
            _currency.Items.Clear();
            foreach (var c in _currencies.Where(x => x.Code != CurrencyCodes.Irr))
            {
                _currency.Items.Add(new ComboItem(c.Code, $"{c.Code} - {c.Name}"));
            }
            UiHelpers.SelectByValue(_currency, previousCurrency);

            var previousSettlementCurrency = (_settlementCurrency.SelectedItem as ComboItem)?.Value;
            _settlementCurrency.Items.Clear();
            foreach (var c in _currencies)
            {
                _settlementCurrency.Items.Add(new ComboItem(c.Code, $"{c.Code} - {c.Name}"));
            }
            UiHelpers.SelectByValue(_settlementCurrency, previousSettlementCurrency ?? CurrencyCodes.Irr);
            SetupSettlementGrid();

            var previousBranch = (_branch.SelectedItem as ComboItem)?.Value;
            UiHelpers.FillBranches(_branch, _branches, _user, includeAll: false, selectedValue: previousBranch);

            var previousFilter = (_filterBranch.SelectedItem as ComboItem)?.Value;
            UiHelpers.FillBranches(_filterBranch, _readableBranches, _user, includeAll: true, selectedValue: previousFilter);
        }
        finally
        {
            _filling = false;
        }

        FillRateFromSelection();
        UpdateSettlementControls();
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
            SettlementModeText(t.SettlementMode),
            SettlementSummary(t),
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

    /// <summary>نرخ فرم را از نرخ روز ارز و شعبه‌ی انتخابی پر می‌کند؛ اگر نرخی نباشد، کادر خالی می‌شود تا نرخ ارز قبلی باقی نماند.</summary>
    /// <summary>بارگذاری معامله‌ی انتخاب‌شده در فرم برای ویرایش (مدیر).</summary>
    private void StartEdit()
    {
        var trade = SelectedTrade();
        if (trade is null || trade.IsVoided)
        {
            UiHelpers.ShowInfo(this, "یک معامله‌ی فعال را در جدول انتخاب کنید.");
            return;
        }

        _filling = true;
        try
        {
            _editingTradeId = trade.Id;
            _buy.Checked = trade.Type == TradeType.Buy;
            _sell.Checked = trade.Type == TradeType.Sell;
            UiHelpers.SelectByValue(_branch, trade.BranchId.ToString(CultureInfo.InvariantCulture));
            UiHelpers.SelectByValue(_currency, trade.CurrencyCode);
            _amount.Text = trade.Amount.ToString("0.####", CultureInfo.InvariantCulture);
            _rate.Text = trade.Rate.ToString("0.####", CultureInfo.InvariantCulture);
            _fee.Text = trade.FeeIrr.ToString("0", CultureInfo.InvariantCulture);
            UiHelpers.SelectByValue(_settlementMode, trade.SettlementMode switch
            {
                TradeSettlementMode.Direct => "DIRECT",
                TradeSettlementMode.Split => "SPLIT",
                TradeSettlementMode.CustomerAccount => "ACCOUNT",
                _ => "DIRECT",
            });
            UiHelpers.SelectByValue(_rateMode, trade.RateMode == TradeRateMode.Direct ? "DIRECT" : "DERIVED");
            UiHelpers.SelectByValue(_settlementCurrency, trade.SettlementCurrencyCode ?? CurrencyCodes.Irr);
            _crossRate.Text = trade.CrossRate > 0m ? trade.CrossRate.ToString("0.########", CultureInfo.InvariantCulture) : string.Empty;
            _applyOffset.Checked = trade.CustomerOffsetIrr > 0m;
            FillSettlementGrid(trade.Settlements ?? Array.Empty<TradeSettlementInfo>());
            SelectCustomer(trade.CustomerId, trade.CustomerName);
            _note.Text = trade.Note ?? string.Empty;
            _occurredOn.Text = PersianDate.FormatDate(trade.OccurredAt);
        }
        finally
        {
            _filling = false;
        }

        _submit.Text = "ذخیره‌ی ویرایش";
        _cancelEdit.Visible = true;
        UpdateSettlementControls();
        UpdatePreview();
    }

    private void CancelEdit()
    {
        _editingTradeId = null;
        _submit.Text = "ثبت معامله";
        _cancelEdit.Visible = false;
        _amount.Clear();
        _fee.Text = "0";
        UiHelpers.SelectByValue(_settlementMode, "DIRECT");
        UiHelpers.SelectByValue(_rateMode, "DERIVED");
        UiHelpers.SelectByValue(_settlementCurrency, CurrencyCodes.Irr);
        _crossRate.Clear();
        _applyOffset.Checked = false;
        _settlementsGrid.Rows.Clear();
        _customerBox.SelectedIndex = -1;
        _note.Clear();
        _occurredOn.Clear();
        FillRateFromSelection();
        UpdateSettlementControls();
        UpdatePreview();
    }

    private void SetupSettlementGrid()
    {
        _settlementsGrid.Rows.Clear();
        _settlementsGrid.Columns.Clear();
        var currencyItems = _currencies.Select(c => new ComboItem(c.Code, $"{c.Code} - {c.Name}")).ToList();
        var currencyColumn = new DataGridViewComboBoxColumn
        {
            Name = "CurrencyCode",
            HeaderText = "ارز",
            DataSource = currencyItems,
            DisplayMember = nameof(ComboItem.Text),
            ValueMember = nameof(ComboItem.Value),
            ValueType = typeof(string),
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        var amountColumn = new DataGridViewTextBoxColumn
        {
            Name = "Amount",
            HeaderText = "مقدار",
            ValueType = typeof(string),
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        _settlementsGrid.Columns.AddRange(currencyColumn, amountColumn);
    }

    private void FillSettlementGrid(IReadOnlyList<TradeSettlementInfo> lines)
    {
        _settlementsGrid.Rows.Clear();
        foreach (var line in lines)
        {
            var index = _settlementsGrid.Rows.Add();
            var row = _settlementsGrid.Rows[index];
            row.Cells["CurrencyCode"].Value = line.CurrencyCode;
            row.Cells["Amount"].Value = line.Amount.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }

    private void FillRateFromSelection()
    {
        if (_filling || _currency.SelectedItem is not ComboItem item || UiHelpers.SelectedBranchId(_branch) is not { } branchId)
        {
            return;
        }
        var baseRate = _rates.FirstOrDefault(r => r.BranchId == branchId && r.CurrencyCode == item.Value);
        if (baseRate is null)
        {
            _rate.Text = string.Empty;
            return;
        }
        var baseValue = _sell.Checked ? baseRate.SellRateIrr : baseRate.BuyRateIrr;
        _rate.Text = MoneyMath.FormatRate(baseValue);

        if (SelectedValue(_settlementMode) == "DIRECT" && SelectedValue(_rateMode) == "DERIVED"
            && _settlementCurrency.SelectedItem is ComboItem counter)
        {
            var counterRate = counter.Value == CurrencyCodes.Irr
                ? 1m
                : _rates.FirstOrDefault(r => r.BranchId == branchId && r.CurrencyCode == counter.Value) is { } quote
                    ? (_sell.Checked ? quote.BuyRateIrr : quote.SellRateIrr)
                    : 0m;
            _crossRate.Text = counterRate > 0m
                ? MoneyMath.RoundTo(baseValue / counterRate, 8).ToString("0.########", CultureInfo.InvariantCulture)
                : string.Empty;
        }
    }

    private void UpdateSettlementControls(bool updateRate = false)
    {
        var direct = SelectedValue(_settlementMode) == "DIRECT";
        var split = SelectedValue(_settlementMode) == "SPLIT";
        _settlementCurrency.Visible = direct;
        _settlementCurrencyLabel.Visible = direct;
        _rateMode.Visible = direct;
        _rateModeLabel.Visible = direct;
        _crossRate.Visible = direct;
        _crossRateLabel.Visible = direct;
        _settlementsGrid.Visible = split;
        _settlementsLabel.Visible = split;
        _crossRate.ReadOnly = direct && SelectedValue(_rateMode) == "DERIVED";
        if (updateRate && direct && SelectedValue(_rateMode) == "DERIVED")
        {
            FillRateFromSelection();
        }
        UpdatePreview();
    }

    private static string? SelectedValue(ComboBox combo) => (combo.SelectedItem as ComboItem)?.Value;

    private void UpdatePreview()
    {
        if (!InputParser.TryParseDecimal(_amount.Text, out var amount) || !InputParser.TryParseDecimal(_rate.Text, out var rate))
        {
            _preview.Text = string.Empty;
            return;
        }

        var irr = MoneyMath.RoundIrr(amount * rate);
        var fee = InputParser.TryParseDecimal(_fee.Text, out var parsedFee) ? parsedFee : 0m;
        var due = _sell.Checked ? irr + fee : irr - fee;
        var mode = SelectedValue(_settlementMode);
        if (mode == "DIRECT" && _settlementCurrency.SelectedItem is ComboItem counter)
        {
            var counterRate = SettlementRateIrr(counter.Value);
            if (SelectedValue(_rateMode) == "DIRECT" && InputParser.TryParseDecimal(_crossRate.Text, out var directRate) && counterRate > 0m)
            {
                irr = MoneyMath.RoundIrr(amount * directRate * counterRate);
                due = _sell.Checked ? irr + fee : irr - fee;
            }
            var quantity = counterRate > 0m
                ? MoneyMath.RoundTo(due / counterRate, CurrencyDecimals(counter.Value))
                : 0m;
            _preview.Text = $"ارزش معامله: {MoneyMath.FormatAmount(irr, 0)} ریال   |   تسویه‌ی مستقیم: {MoneyMath.FormatAmount(quantity, CurrencyDecimals(counter.Value))} {counter.Value} (پیش از تهاتر)";
        }
        else if (mode == "SPLIT")
        {
            _preview.Text = $"ارزش معامله: {MoneyMath.FormatAmount(irr, 0)} ریال   |   تسویه‌ی چندبخشی؛ مانده‌ی تسویه‌نشده روی حساب مشتری می‌ماند.";
        }
        else
        {
            _preview.Text = $"ارزش معامله: {MoneyMath.FormatAmount(irr, 0)} ریال   |   مبلغ {MoneyMath.FormatAmount(due, 0)} ریال روی حساب مشتری ثبت می‌شود.";
        }
    }

    private decimal SettlementRateIrr(string currencyCode)
    {
        if (currencyCode == CurrencyCodes.Irr)
        {
            return 1m;
        }
        if (UiHelpers.SelectedBranchId(_branch) is not { } branchId)
        {
            return 0m;
        }
        var quote = _rates.FirstOrDefault(r => r.BranchId == branchId && r.CurrencyCode == currencyCode);
        if (quote is null)
        {
            return 0m;
        }
        return _buy.Checked ? quote.SellRateIrr : quote.BuyRateIrr;
    }

    private int CurrencyDecimals(string currencyCode) =>
        _currencies.FirstOrDefault(c => c.Code == currencyCode)?.DecimalPlaces ?? 4;

    private TradeInput BuildTradeInput(int branchId, string currencyCode, decimal amount, decimal rate, decimal fee, int? customerId)
    {
        var mode = SelectedValue(_settlementMode) switch
        {
            "DIRECT" => TradeSettlementMode.Direct,
            "SPLIT" => TradeSettlementMode.Split,
            "ACCOUNT" => TradeSettlementMode.CustomerAccount,
            _ => throw new BusinessRuleException("روش تسویه‌ی معامله را انتخاب کنید."),
        };
        var rateMode = mode == TradeSettlementMode.Direct && SelectedValue(_rateMode) == "DIRECT"
            ? TradeRateMode.Direct
            : TradeRateMode.Derived;
        decimal? crossRate = null;
        if (rateMode == TradeRateMode.Direct)
        {
            if (!InputParser.TryParseDecimal(_crossRate.Text, out var parsedCrossRate))
            {
                throw new BusinessRuleException("نرخ مستقیم جفت‌ارز را وارد کنید.");
            }
            crossRate = parsedCrossRate;
        }

        var settlementLines = new List<TradeSettlementInput>();
        if (mode == TradeSettlementMode.Split)
        {
            _settlementsGrid.EndEdit();
            foreach (DataGridViewRow row in _settlementsGrid.Rows)
            {
                if (row.IsNewRow)
                {
                    continue;
                }
                var code = Convert.ToString(row.Cells["CurrencyCode"].Value, CultureInfo.InvariantCulture)?.Trim().ToUpperInvariant() ?? string.Empty;
                var amountText = Convert.ToString(row.Cells["Amount"].Value, CultureInfo.InvariantCulture);
                if (code.Length == 0 && string.IsNullOrWhiteSpace(amountText))
                {
                    continue;
                }
                if (code.Length == 0)
                {
                    throw new BusinessRuleException("برای هر سطر تسویه‌ی چندبخشی، ارز را انتخاب کنید.");
                }
                if (!InputParser.TryParseDecimal(amountText, out var lineAmount))
                {
                    throw new BusinessRuleException($"مقدار سطر تسویه‌ی {code} را وارد کنید.");
                }
                settlementLines.Add(new TradeSettlementInput(code, lineAmount));
            }
        }

        return new TradeInput(
            branchId,
            currencyCode,
            amount,
            rate,
            null,
            null,
            _note.Text,
            fee,
            customerId,
            mode,
            rateMode,
            mode == TradeSettlementMode.Direct ? SelectedValue(_settlementCurrency) : null,
            crossRate,
            settlementLines,
            _applyOffset.Checked);
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
            var hasRate = InputParser.TryParseDecimal(_rate.Text, out var rate);
            if (!hasRate && (SelectedValue(_settlementMode) != "DIRECT" || !string.IsNullOrWhiteSpace(_rate.Text)))
            {
                throw new BusinessRuleException("نرخ را به‌درستی وارد کنید.");
            }
            if (!hasRate)
            {
                rate = 0m;
            }
            var fee = 0m;
            if (!string.IsNullOrWhiteSpace(_fee.Text) && !InputParser.TryParseDecimal(_fee.Text, out fee))
            {
                throw new BusinessRuleException("کارمزد را به‌درستی وارد کنید (عدد ریال).");
            }

            DateTime? occurredOn = null;
            if (!string.IsNullOrWhiteSpace(_occurredOn.Text))
            {
                if (!PersianDate.TryParseDate(_occurredOn.Text, out var parsedDate))
                {
                    throw new BusinessRuleException("تاریخ معامله را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
                }
                occurredOn = parsedDate;
            }

            int? customerId = int.TryParse((_customerBox.SelectedItem as ComboItem)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var selectedCustomer)
                ? selectedCustomer
                : null;
            var input = BuildTradeInput(branchId, item.Value, amount, rate, fee, customerId);
            var type = _sell.Checked ? TradeType.Sell : TradeType.Buy;
            if (_editingTradeId is { } editId)
            {
                await _services.Trades.EditTradeAsync(_user, editId, input, type, occurredOn, DateTime.Now);
                CancelEdit();
                UiHelpers.ShowInfo(this, $"معامله‌ی شماره {editId} ویرایش شد. اگر مبلغ یا نرخ تغییر کرده، نسخه‌ی اصلاحی جایگزین شده است.");
            }
            else
            {
                var id = await _services.Trades.RecordTradeAsync(input, type, _user, DateTime.Now, occurredOn);
                _amount.Clear();
                _fee.Text = "0";
                _applyOffset.Checked = false;
                _crossRate.Clear();
                _settlementsGrid.Rows.Clear();
                _note.Clear();
                _occurredOn.Clear();
                UiHelpers.ShowInfo(this, $"معامله شماره {id} ثبت شد.");
            }
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

    private async Task LoadCustomersAsync()
    {
        var keep = (_customerBox.SelectedItem as ComboItem)?.Value;
        IReadOnlyList<CustomerInfo> customers;
        try
        {
            customers = await _services.Customers.SearchAsync(_user, null, take: 2000);
        }
        catch (BusinessRuleException)
        {
            customers = Array.Empty<CustomerInfo>();
        }

        _customerBox.BeginUpdate();
        _customerBox.Items.Clear();
        foreach (var customer in customers)
        {
            _customerBox.Items.Add(new ComboItem(customer.Id.ToString(CultureInfo.InvariantCulture), CustomerLabel(customer)));
        }
        _customerBox.EndUpdate();

        if (int.TryParse(keep, NumberStyles.None, CultureInfo.InvariantCulture, out var keepId))
        {
            SelectCustomer(keepId, null);
        }
    }

    /// <summary>
    /// مشتری را انتخاب می‌کند. اگر در فهرست نباشد (مثلاً بیش از سقف فهرست)، به فهرست اضافه می‌شود تا
    /// معامله‌ی قدیمی با مشتری اشتباه ویرایش نشود.
    /// </summary>
    private void SelectCustomer(int? customerId, string? fallbackName)
    {
        if (customerId is null)
        {
            _customerBox.SelectedIndex = -1;
            return;
        }

        var value = customerId.Value.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < _customerBox.Items.Count; i++)
        {
            if (_customerBox.Items[i] is ComboItem item && item.Value == value)
            {
                _customerBox.SelectedIndex = i;
                return;
            }
        }

        var fallback = new ComboItem(value, fallbackName ?? $"مشتری شماره {value}");
        _customerBox.Items.Add(fallback);
        _customerBox.SelectedItem = fallback;
    }

    private async Task AddCustomerAsync()
    {
        using var dialog = new CustomerDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null)
        {
            return;
        }
        try
        {
            var id = await _services.Customers.CreateAsync(_user, dialog.Result, DateTime.Now);
            await LoadCustomersAsync();
            SelectCustomer(id, null);
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private static string CustomerLabel(CustomerInfo customer) =>
        $"{customer.FullName} · {customer.CustomerCode}";

    private static string SettlementModeText(TradeSettlementMode mode) => mode switch
    {
        TradeSettlementMode.Direct => "مستقیم",
        TradeSettlementMode.Split => "چندبخشی",
        TradeSettlementMode.CustomerAccount => "حساب مشتری",
        _ => "نامشخص",
    };

    private static string SettlementSummary(TradeInfo trade)
    {
        var lines = trade.Settlements ?? Array.Empty<TradeSettlementInfo>();
        var parts = lines.Select(line =>
            $"{(line.Direction == TradeSettlementDirection.Payment ? "پرداخت" : "دریافت")} {MoneyMath.FormatAmount(line.Amount, 4)} {line.CurrencyCode}");
        var summary = string.Join("؛ ", parts);
        if (trade.CustomerOffsetIrr > 0m)
        {
            summary += (summary.Length == 0 ? string.Empty : "؛ ") + "تهاتر " + MoneyMath.FormatAmount(trade.CustomerOffsetIrr, 0) + " ریال";
        }
        if (summary.Length == 0)
        {
            summary = trade.SettlementMode == TradeSettlementMode.CustomerAccount ? "روی حساب مشتری" : "بدون وجه نقد";
        }
        return summary;
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
