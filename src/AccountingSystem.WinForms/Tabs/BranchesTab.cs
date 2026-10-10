using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

internal sealed class BranchesTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly TextBox _code = new() { Width = 100, MaxLength = 10 };
    private readonly TextBox _name = new() { Width = 220, MaxLength = 100 };
    private readonly Button _create = new() { Text = "ایجاد شعبه", AutoSize = true };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();

    public BranchesTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        var inputs = UiHelpers.CreateInputPanel();
        inputs.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("کد شعبه (۱ تا ۱۰ حرف یا رقم انگلیسی):"), _code,
            UiHelpers.MakeLabel("نام شعبه:"), _name,
            _create,
        });

        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(12, 10, 12, 0),
            Text = "پس از ایجاد شعبه، برای آن صندوق‌ها و موجودی ارزی به تعداد همه‌ی ارزها ساخته می‌شود. موجودی اولیه‌ی صندوق‌های شعبه را در تب «صندوق‌ها» ثبت کنید.",
        };

        Controls.Add(_grid);
        Controls.Add(note);
        Controls.Add(inputs);

        _create.Click += async (_, _) => await CreateAsync();
    }

    public async Task RefreshAsync()
    {
        var branches = await _services.Branches.GetBranchesAsync();
        var rows = branches.Select(b => new[]
        {
            b.Code,
            b.Name,
            PersianDate.FormatDateTime(b.CreatedAt),
        });
        UiHelpers.Fill(_grid, new[] { "کد شعبه", "نام شعبه", "تاریخ ایجاد (شمسی)" }, rows);
    }

    private async Task CreateAsync()
    {
        try
        {
            await _services.Branches.CreateBranchAsync(_user, _code.Text.Trim(), _name.Text.Trim(), DateTime.Now);
            _code.Clear();
            _name.Clear();
            UiHelpers.ShowInfo(this, "شعبه‌ی جدید ایجاد شد. موجودی اولیه‌ی صندوق‌های آن را در تب «صندوق‌ها» ثبت کنید.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
