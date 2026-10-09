using System.Globalization;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>ثبت، ویرایش، ابطال و فهرست مستقل رسیدهای دریافت و پرداخت مشتریان.</summary>
internal sealed class CashTransactionsTab : UserControl, IRefreshable
{
    private static readonly string[] Headers =
    {
        "شماره", "تاریخ (شمسی)", "شعبه", "نوع", "مشتری", "ارز", "مقدار", "روش نرخ", "ارزش ریالی", "ثبت‌کننده", "وضعیت",
    };

    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly ComboBox _branch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly ComboBox _direction = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _customer = new()
    {
        Width = 245,
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems,
    };
    private readonly ComboBox _currency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly TextBox _amount = new() { Width = 105 };
    private readonly ComboBox _rateMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
    private readonly TextBox _rate = new() { Width = 110 };
    private readonly Label _rateLabel = UiHelpers.MakeLabel("نرخ توافقی:");
    private readonly TextBox _occurredOn = new() { Width = 110, PlaceholderText = "امروز" };
    private readonly TextBox _note = new() { Width = 165 };
    private readonly Label _preview = UiHelpers.MakeLabel(string.Empty);
    private readonly Button _submit = new() { Text = "ثبت دریافت/پرداخت", AutoSize = true };
    private readonly Button _cancelEdit = new() { Text = "انصراف از ویرایش", AutoSize = true, Visible = false };
    private readonly FlowLayoutPanel _inputPanel = UiHelpers.CreateInputPanel();

    private readonly TextBox _from = new() { Width = 110, PlaceholderText = "از تاریخ" };
    private readonly TextBox _to = new() { Width = 110, PlaceholderText = "تا تاریخ" };
    private readonly ComboBox _filterBranch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly Button _show = new() { Text = "نمایش", AutoSize = true };
    private readonly Button _edit = new() { Text = "ویرایش انتخاب‌شده", AutoSize = true };
    private readonly Button _void = new() { Text = "ابطال انتخاب‌شده", AutoSize = true };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();

    private IReadOnlyList<BranchInfo> _branches = Array.Empty<BranchInfo>();
    private IReadOnlyList<BranchInfo> _readableBranches = Array.Empty<BranchInfo>();
    private IReadOnlyList<CurrencyInfo> _currencies = Array.Empty<CurrencyInfo>();
    private IReadOnlyList<RateInfo> _rates = Array.Empty<RateInfo>();
    private IReadOnlyList<CustomerInfo> _customers = Array.Empty<CustomerInfo>();
    private IReadOnlyList<CashTransactionInfo> _transactions = Array.Empty<CashTransactionInfo>();
    private long? _editingId;
    private int? _editingBranchId;
    private bool _canCreateSomewhere;
    private bool _filling;

    public CashTransactionsTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;
        _direction.Items.AddRange(new object[]
        {
            new ComboItem("Receipt", "دریافت از مشتری"),
            new ComboItem("Payment", "پرداخت به مشتری"),
        });
        _rateMode.Items.AddRange(new object[]
        {
            new ComboItem("DERIVED", "نرخ روز شعبه"),
            new ComboItem("DIRECT", "نرخ توافقی"),
        });
        _direction.SelectedIndex = 0;
        _rateMode.SelectedIndex = 0;

        _inputPanel.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("شعبه:"), _branch,
            UiHelpers.MakeLabel("نوع سند:"), _direction,
            UiHelpers.MakeLabel("مشتری:"), _customer,
            UiHelpers.MakeLabel("ارز:"), _currency,
            UiHelpers.MakeLabel("مقدار:"), _amount,
            UiHelpers.MakeLabel("روش نرخ:"), _rateMode,
            _rateLabel, _rate,
            UiHelpers.MakeLabel("تاریخ (شمسی):"), _occurredOn,
            UiHelpers.MakeLabel("توضیحات:"), _note,
            _preview, _submit, _cancelEdit,
        });

        var filters = UiHelpers.CreateInputPanel();
        filters.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("از تاریخ:"), _from,
            UiHelpers.MakeLabel("تا تاریخ:"), _to,
            UiHelpers.MakeLabel("شعبه:"), _filterBranch,
            _show, _edit, _void,
        });
        _edit.Visible = false;
        _void.Visible = false;

        Controls.Add(_grid);
        Controls.Add(filters);
        Controls.Add(_inputPanel);

        _branch.SelectedIndexChanged += (_, _) => UpdateRateAndPreview();
        _direction.SelectedIndexChanged += (_, _) => UpdateRateAndPreview();
        _currency.SelectedIndexChanged += (_, _) => UpdateRateAndPreview();
        _rateMode.SelectedIndexChanged += (_, _) => UpdateRateAndPreview();
        _amount.TextChanged += (_, _) => UpdateRateAndPreview();
        _rate.TextChanged += (_, _) => UpdatePreview();
        _submit.Click += async (_, _) => await SubmitAsync();
        _cancelEdit.Click += (_, _) => CancelEdit();
        _show.Click += async (_, _) => await SafeRefreshAsync();
        _edit.Click += (_, _) => StartEdit();
        _void.Click += async (_, _) => await VoidSelectedAsync();
        _grid.SelectionChanged += async (_, _) =>
        {
            try
            {
                await UpdateActionAvailabilityAsync();
            }
            catch (Exception ex)
            {
                UiHelpers.ShowError(this, ex);
            }
        };
    }

    public async Task RefreshAsync()
    {
        var canCreateSomewhere = await _services.Permissions.HasAnyAsync(_user, Permission.CashTransactionCreate);
        _canCreateSomewhere = canCreateSomewhere;
        var canEditSomewhere = await _services.Permissions.HasAnyAsync(_user, Permission.CashTransactionEdit);
        var canVoidSomewhere = await _services.Permissions.HasAnyAsync(_user, Permission.CashTransactionVoid);
        _edit.Visible = canEditSomewhere;
        _void.Visible = canVoidSomewhere;
        _inputPanel.Visible = canCreateSomewhere || _editingId is not null;
        _submit.Visible = canCreateSomewhere || _editingId is not null;
        _cancelEdit.Visible = _editingId is not null;

        _branches = await _services.Permissions.GetBranchesAsync(_user, Permission.CashTransactionCreate);
        _readableBranches = await _services.Permissions.GetBranchesAsync(_user);
        _currencies = (await _services.Admin.GetCurrenciesAsync()).Where(c => c.IsActive).OrderBy(c => c.Code).ToList();

        try
        {
            _customers = await _services.Customers.SearchAsync(_user, null, take: 2000);
        }
        catch (BusinessRuleException)
        {
            _customers = Array.Empty<CustomerInfo>();
        }

        var rates = new List<RateInfo>();
        foreach (var branch in _readableBranches)
        {
            rates.AddRange(await _services.Admin.GetLatestRatesAsync(_user, branch.Id));
        }
        _rates = rates;

        var selectedBranch = (_branch.SelectedItem as ComboItem)?.Value;
        var selectedCurrency = (_currency.SelectedItem as ComboItem)?.Value;
        _filling = true;
        try
        {
            UiHelpers.FillBranches(_branch, _branches, _user, includeAll: false, selectedValue: selectedBranch);
            UiHelpers.FillBranches(_filterBranch, _readableBranches, _user, includeAll: true,
                selectedValue: (_filterBranch.SelectedItem as ComboItem)?.Value);

            _currency.Items.Clear();
            foreach (var currency in _currencies)
            {
                _currency.Items.Add(new ComboItem(currency.Code, $"{currency.Code} - {currency.Name}"));
            }
            UiHelpers.SelectByValue(_currency, selectedCurrency ?? CurrencyCodes.Irr);

            var previousCustomer = (_customer.SelectedItem as ComboItem)?.Value;
            _customer.Items.Clear();
            foreach (var customer in _customers)
            {
                _customer.Items.Add(new ComboItem(customer.Id.ToString(CultureInfo.InvariantCulture),
                    $"{customer.FullName} · {customer.CustomerCode}"));
            }
            UiHelpers.SelectByValue(_customer, previousCustomer);
        }
        finally
        {
            _filling = false;
        }

        UpdateRateAndPreview();
        await LoadTransactionsAsync();
    }

    private async Task LoadTransactionsAsync()
    {
        var (from, to) = UiHelpers.ParseJalaliRange(_from, _to);
        _transactions = await _services.CashTransactions.GetTransactionsAsync(
            _user,
            UiHelpers.SelectedBranchId(_filterBranch),
            from,
            to);
        var rows = _transactions.Select(transaction => new[]
        {
            transaction.Id.ToString(CultureInfo.InvariantCulture),
            PersianDate.FormatDateTime(transaction.OccurredAt),
            transaction.BranchCode,
            transaction.Direction == CashTransactionDirection.Receipt ? "دریافت" : "پرداخت",
            transaction.CustomerName,
            transaction.CurrencyCode,
            MoneyMath.FormatAmount(transaction.Amount, transaction.DecimalPlaces),
            transaction.RateMode == TradeRateMode.Derived ? "نرخ روز" : "توافقی " + MoneyMath.FormatRate(transaction.RateIrr),
            MoneyMath.FormatAmount(transaction.IrrAmount, 0),
            transaction.CreatedBy,
            transaction.IsVoided ? "باطل شد: " + transaction.VoidReason : "فعال",
        });
        UiHelpers.Fill(_grid, Headers, rows);
        await UpdateActionAvailabilityAsync();
    }

    private CashTransactionInfo? SelectedTransaction()
    {
        var index = _grid.CurrentRow?.Index ?? -1;
        return index >= 0 && index < _transactions.Count ? _transactions[index] : null;
    }

    private async Task UpdateActionAvailabilityAsync()
    {
        _edit.Enabled = false;
        _void.Enabled = false;
        if (SelectedTransaction() is not { IsVoided: false } transaction)
        {
            return;
        }
        _edit.Enabled = await _services.Permissions.HasAsync(_user, Permission.CashTransactionEdit, transaction.BranchId);
        _void.Enabled = await _services.Permissions.HasAsync(_user, Permission.CashTransactionVoid, transaction.BranchId);
    }

    private void StartEdit()
    {
        var transaction = SelectedTransaction();
        if (transaction is null || transaction.IsVoided)
        {
            UiHelpers.ShowInfo(this, "یک سند فعال را از فهرست انتخاب کنید.");
            return;
        }

        _filling = true;
        try
        {
            _editingId = transaction.Id;
            _editingBranchId = transaction.BranchId;
            _branch.Enabled = false;
            _inputPanel.Visible = true;
            _submit.Visible = true;
            UiHelpers.SelectByValue(_branch, transaction.BranchId.ToString(CultureInfo.InvariantCulture));
            UiHelpers.SelectByValue(_direction, transaction.Direction.ToString());
            UiHelpers.SelectByValue(_currency, transaction.CurrencyCode);
            SelectCustomer(transaction.CustomerId, transaction.CustomerName, transaction.CustomerCode);
            _amount.Text = transaction.Amount.ToString("0.####", CultureInfo.InvariantCulture);
            UiHelpers.SelectByValue(_rateMode, transaction.RateMode == TradeRateMode.Direct ? "DIRECT" : "DERIVED");
            _rate.Text = transaction.RateIrr.ToString("0.####", CultureInfo.InvariantCulture);
            _occurredOn.Text = PersianDate.FormatDate(transaction.OccurredAt);
            _note.Text = transaction.Note ?? string.Empty;
        }
        finally
        {
            _filling = false;
        }
        _submit.Text = "ذخیره‌ی ویرایش";
        _cancelEdit.Visible = true;
        UpdateRateAndPreview();
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editingBranchId = null;
        _branch.Enabled = true;
        _inputPanel.Visible = _canCreateSomewhere;
        _submit.Visible = _canCreateSomewhere;
        _submit.Text = "ثبت دریافت/پرداخت";
        _cancelEdit.Visible = false;
        _amount.Clear();
        _rate.Clear();
        _occurredOn.Clear();
        _note.Clear();
        UiHelpers.SelectByValue(_direction, "Receipt");
        UiHelpers.SelectByValue(_rateMode, "DERIVED");
        UiHelpers.SelectByValue(_currency, CurrencyCodes.Irr);
        _customer.SelectedIndex = -1;
        UpdateRateAndPreview();
    }

    private void UpdateRateAndPreview()
    {
        if (_filling)
        {
            return;
        }
        var code = SelectedValue(_currency);
        var isIrr = code == CurrencyCodes.Irr;
        _rateMode.Enabled = !isIrr;
        if (isIrr)
        {
            UiHelpers.SelectByValue(_rateMode, "DERIVED");
            _rateLabel.Text = "نرخ ریال: ۱";
            _rate.Visible = false;
            _rateLabel.Visible = true;
            _preview.Text = string.Empty;
            UpdatePreview(1m);
            return;
        }

        if (SelectedValue(_rateMode) == "DIRECT")
        {
            _rateLabel.Text = "نرخ توافقی:";
            _rate.Visible = true;
            UpdatePreview(InputParser.TryParseDecimal(_rate.Text, out var directRate) ? directRate : null);
            return;
        }

        _rate.Visible = false;
        var branchId = _editingBranchId ?? UiHelpers.SelectedBranchId(_branch);
        var quote = _rates.FirstOrDefault(r => r.BranchId == branchId && r.CurrencyCode == code);
        if (quote is null)
        {
            _rateLabel.Text = "برای این شعبه نرخ روز ثبت نشده است.";
            UpdatePreview(null);
            return;
        }
        var rate = SelectedValue(_direction) == "Receipt" ? quote.BuyRateIrr : quote.SellRateIrr;
        _rateLabel.Text = $"نرخ روز: {MoneyMath.FormatRate(rate)} ریال";
        UpdatePreview(rate);
    }

    private void UpdatePreview(decimal? rate = null)
    {
        if (rate is null && SelectedValue(_rateMode) == "DIRECT" && InputParser.TryParseDecimal(_rate.Text, out var directRate))
        {
            rate = directRate;
        }
        if (rate is null || !InputParser.TryParseDecimal(_amount.Text, out var amount) || amount <= 0)
        {
            _preview.Text = string.Empty;
            return;
        }
        var irr = MoneyMath.RoundIrr(amount * rate.Value);
        _preview.Text = $"ارزش ریالی تقریبی: {MoneyMath.FormatAmount(irr, 0)} ریال";
    }

    private async Task SubmitAsync()
    {
        try
        {
            var branchId = _editingId is null ? UiHelpers.RequiredBranchId(_branch) : _editingBranchId ?? UiHelpers.RequiredBranchId(_branch);
            var direction = SelectedValue(_direction) == "Payment"
                ? CashTransactionDirection.Payment
                : CashTransactionDirection.Receipt;
            var code = SelectedValue(_currency) ?? throw new BusinessRuleException("ارز را انتخاب کنید.");
            if (!InputParser.TryParseDecimal(_amount.Text, out var amount))
            {
                throw new BusinessRuleException("مقدار را به‌درستی وارد کنید.");
            }
            if (!int.TryParse(SelectedValue(_customer), NumberStyles.None, CultureInfo.InvariantCulture, out var customerId))
            {
                throw new BusinessRuleException("مشتری را انتخاب کنید.");
            }

            var rateMode = code == CurrencyCodes.Irr || SelectedValue(_rateMode) != "DIRECT"
                ? TradeRateMode.Derived
                : TradeRateMode.Direct;
            decimal? rate = null;
            if (rateMode == TradeRateMode.Direct)
            {
                if (!InputParser.TryParseDecimal(_rate.Text, out var directRate))
                {
                    throw new BusinessRuleException("نرخ توافقی را وارد کنید.");
                }
                rate = directRate;
            }

            DateTime? occurredOn = null;
            if (!string.IsNullOrWhiteSpace(_occurredOn.Text))
            {
                if (!PersianDate.TryParseDate(_occurredOn.Text, out var parsedDate))
                {
                    throw new BusinessRuleException("تاریخ را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
                }
                occurredOn = parsedDate;
            }

            var input = new CashTransactionInput(branchId, direction, customerId, code, amount, rateMode, rate, _note.Text);
            if (_editingId is { } id)
            {
                await _services.CashTransactions.EditAsync(_user, id, input, occurredOn, DateTime.Now);
                UiHelpers.ShowInfo(this, $"دریافت/پرداخت شماره {id} ویرایش شد؛ نسخه‌ی قبلی با سند معکوس حفظ شد.");
                CancelEdit();
            }
            else
            {
                var transactionId = await _services.CashTransactions.RecordAsync(_user, input, DateTime.Now, occurredOn);
                UiHelpers.ShowInfo(this, $"دریافت/پرداخت شماره {transactionId} ثبت شد.");
                _amount.Clear();
                _rate.Clear();
                _occurredOn.Clear();
                _note.Clear();
            }
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
            var transaction = SelectedTransaction() ?? throw new BusinessRuleException("یک سند را از فهرست انتخاب کنید.");
            if (transaction.IsVoided)
            {
                throw new BusinessRuleException("این سند قبلاً باطل شده است.");
            }
            var reason = UiHelpers.PromptText(this, "ابطال دریافت/پرداخت", $"دلیل ابطال سند شماره {transaction.Id} را وارد کنید:");
            if (string.IsNullOrWhiteSpace(reason))
            {
                return;
            }
            if (MessageBox.Show(this, "سند معکوس ثبت و سابقه حفظ می‌شود. ادامه می‌دهید؟", "تأیید ابطال",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            await _services.CashTransactions.VoidAsync(_user, transaction.Id, reason, DateTime.Now);
            UiHelpers.ShowInfo(this, $"دریافت/پرداخت شماره {transaction.Id} باطل شد.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private void SelectCustomer(int customerId, string fallbackName, string customerCode)
    {
        var value = customerId.ToString(CultureInfo.InvariantCulture);
        if (_customer.Items.Cast<object>().OfType<ComboItem>().All(item => item.Value != value))
        {
            _customer.Items.Add(new ComboItem(value, $"{fallbackName} · {customerCode}"));
        }
        UiHelpers.SelectByValue(_customer, value);
    }

    private static string? SelectedValue(ComboBox combo) => (combo.SelectedItem as ComboItem)?.Value;

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
