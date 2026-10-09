using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>
/// فهرست مشتریان مشترک صرافی. همه‌ی کاربران فعال فهرست را می‌بینند؛ ثبت و ویرایش برای دارندگان
/// وظیفه‌ی «ثبت معامله» در هر شعبه و مدیر سیستم فعال است. شماره‌ی کارت در فهرست ماسک می‌شود.
/// </summary>
internal sealed class CustomersTab : UserControl, IRefreshable
{
    private static readonly string[] Headers =
    {
        "کد مشتری", "نام و نام خانوادگی", "کد ملی / شناسه", "موبایل", "تلفن ثابت", "شهر",
        "کارت‌ها", "آخرین به‌روزرسانی (شمسی)",
    };

    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly TextBox _search = new() { Width = 300, PlaceholderText = "نام، کد مشتری، کد ملی، موبایل یا کارت" };
    private readonly Button _find = new() { Text = "جستجو", AutoSize = true };
    private readonly Button _new = new() { Text = "مشتری تازه", AutoSize = true, Enabled = false };
    private readonly Button _edit = new() { Text = "ویرایش", AutoSize = true, Enabled = false };
    private readonly Label _status = UiHelpers.MakeLabel(string.Empty);
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<CustomerInfo> _customers = Array.Empty<CustomerInfo>();
    private bool _canEdit;

    public CustomersTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(12, 10, 12, 0),
            Text = "فهرست مشترک همه‌ی شعبه‌ها. کد مشتری را سیستم می‌سازد و قابل تغییر نیست. " +
                   "نام ثبت‌شده در معاملات قبلی با تغییر اطلاعات مشتری عوض نمی‌شود.",
        };
        var bar = UiHelpers.CreateInputPanel();
        bar.Controls.AddRange(new Control[] { UiHelpers.MakeLabel("جستجو:"), _search, _find, _new, _edit, _status });

        Controls.Add(_grid);
        Controls.Add(hint);
        Controls.Add(bar);

        _find.Click += async (_, _) => await RefreshAsync();
        _search.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await RefreshAsync();
            }
        };
        _new.Click += async (_, _) => await CreateAsync();
        _edit.Click += async (_, _) => await EditSelectedAsync();
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                await EditSelectedAsync();
            }
        };
    }

    public async Task RefreshAsync()
    {
        try
        {
            _canEdit = await _services.Customers.CanEditAsync(_user);
            _new.Enabled = _canEdit;
            _edit.Enabled = _canEdit;
            _customers = await _services.Customers.SearchAsync(_user, _search.Text);
            var rows = _customers.Select(c => new[]
            {
                c.CustomerCode,
                c.FullName,
                c.NationalCode ?? string.Empty,
                c.Mobile ?? string.Empty,
                c.Phone ?? string.Empty,
                c.City ?? string.Empty,
                CustomerRules.MaskCardNumbers(c.CardNumber1, c.CardNumber2) ?? string.Empty,
                PersianDate.FormatDateTime(c.UpdatedAt),
            });
            UiHelpers.Fill(_grid, Headers, rows);
            _status.Text = _canEdit
                ? $"{_customers.Count} مشتری"
                : "فقط مشاهده؛ برای ثبت و ویرایش، وظیفه‌ی «ثبت معامله» لازم است.";
        }
        catch (BusinessRuleException ex)
        {
            _customers = Array.Empty<CustomerInfo>();
            UiHelpers.Fill(_grid, Headers, Array.Empty<string[]>());
            _status.Text = ex.Message;
        }
    }

    private async Task CreateAsync()
    {
        using var dialog = new CustomerDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null)
        {
            return;
        }
        try
        {
            var id = await _services.Customers.CreateAsync(_user, dialog.Result, DateTime.Now);
            var created = await _services.Customers.GetAsync(_user, id);
            UiHelpers.ShowInfo(this, $"مشتری «{created.FullName}» با کد {created.CustomerCode} ثبت شد.");
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private async Task EditSelectedAsync()
    {
        if (!_canEdit)
        {
            UiHelpers.ShowInfo(this, "برای ویرایش مشتری، وظیفه‌ی «ثبت معامله» را در حداقل یک شعبه لازم دارید.");
            return;
        }

        var index = _grid.CurrentRow?.Index ?? -1;
        if (index < 0 || index >= _customers.Count)
        {
            UiHelpers.ShowInfo(this, "یک مشتری را در فهرست انتخاب کنید.");
            return;
        }

        var customer = _customers[index];
        using var dialog = new CustomerDialog(customer);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null)
        {
            return;
        }
        try
        {
            await _services.Customers.UpdateAsync(_user, customer.Id, dialog.Result, DateTime.Now);
            UiHelpers.ShowInfo(this, $"اطلاعات مشتری {customer.CustomerCode} به‌روز شد.");
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
