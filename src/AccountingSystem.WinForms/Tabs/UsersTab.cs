using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

internal sealed class UsersTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly TextBox _username = new() { Width = 150 };
    private readonly TextBox _fullName = new() { Width = 180 };
    private readonly TextBox _password = new() { Width = 150, UseSystemPasswordChar = true };
    private readonly ComboBox _role = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly Button _add = new() { Text = "ثبت کاربر", AutoSize = true };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();

    public UsersTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        _role.Items.Add(new ComboItem(nameof(UserRole.Cashier), "کاربر صندوق"));
        _role.Items.Add(new ComboItem(nameof(UserRole.Admin), "مدیر"));
        _role.SelectedIndex = 0;

        var inputs = UiHelpers.CreateInputPanel();
        inputs.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("نام کاربری:"), _username,
            UiHelpers.MakeLabel("نام و نام خانوادگی:"), _fullName,
            UiHelpers.MakeLabel("رمز عبور:"), _password,
            UiHelpers.MakeLabel("نقش:"), _role,
            _add,
        });

        Controls.Add(_grid);
        Controls.Add(inputs);

        _add.Click += async (_, _) => await AddAsync();
    }

    public async Task RefreshAsync()
    {
        var users = await _services.Users.GetUsersAsync(_user);
        var rows = users.Select(u => new[]
        {
            u.Username,
            u.FullName,
            u.Role == UserRole.Admin ? "مدیر" : "کاربر صندوق",
            u.IsActive ? "فعال" : "غیرفعال",
            PersianDate.FormatDateTime(u.CreatedAt),
        });
        UiHelpers.Fill(_grid, new[] { "نام کاربری", "نام و نام خانوادگی", "نقش", "وضعیت", "تاریخ ثبت (شمسی)" }, rows);
    }

    private async Task AddAsync()
    {
        try
        {
            var role = Enum.Parse<UserRole>(((ComboItem)_role.SelectedItem!).Value);
            await _services.Users.CreateUserAsync(_user, _username.Text, _fullName.Text, _password.Text, role, DateTime.Now);
            _username.Clear();
            _fullName.Clear();
            _password.Clear();
            UiHelpers.ShowInfo(this, "کاربر جدید ثبت شد.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
