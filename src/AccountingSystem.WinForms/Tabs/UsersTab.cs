using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

internal sealed class UsersTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly TextBox _username = new() { Width = 130 };
    private readonly TextBox _fullName = new() { Width = 160 };
    private readonly TextBox _password = new() { Width = 130, PasswordChar = '•' };
    private readonly ComboBox _role = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly ComboBox _branch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly Button _create = new() { Text = "ایجاد کاربر", AutoSize = true };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<BranchInfo> _branches = Array.Empty<BranchInfo>();

    public UsersTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        _role.Items.Add(new ComboItem(nameof(UserRole.Admin), "مدیر (همه‌ی شعبه‌ها)"));
        _role.Items.Add(new ComboItem(nameof(UserRole.Cashier), "کاربر صندوق (یک شعبه)"));
        _role.SelectedIndex = 1;
        _role.SelectedIndexChanged += (_, _) => _branch.Enabled = SelectedRole() == UserRole.Cashier;

        var inputs = UiHelpers.CreateInputPanel();
        inputs.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("نام کاربری:"), _username,
            UiHelpers.MakeLabel("نام کامل:"), _fullName,
            UiHelpers.MakeLabel("گذرواژه:"), _password,
            UiHelpers.MakeLabel("نقش:"), _role,
            UiHelpers.MakeLabel("شعبه:"), _branch,
            _create,
        });
        inputs.Visible = user.Role == UserRole.Admin;

        Controls.Add(_grid);
        Controls.Add(inputs);

        _create.Click += async (_, _) => await CreateAsync();
    }

    public async Task RefreshAsync()
    {
        _branches = await _services.Branches.GetBranchesAsync();
        var previous = (_branch.SelectedItem as ComboItem)?.Value;
        UiHelpers.FillBranches(_branch, _branches, _user, includeAll: false, selectedValue: previous);
        _branch.Enabled = SelectedRole() == UserRole.Cashier;

        var users = await _services.Users.GetUsersAsync(_user);
        var rows = users.Select(u => new[]
        {
            u.Username,
            u.FullName,
            u.Role == UserRole.Admin ? "مدیر" : "کاربر صندوق",
            u.BranchName ?? "همه‌ی شعبه‌ها",
            u.IsActive ? "فعال" : "غیرفعال",
            PersianDate.FormatDateTime(u.CreatedAt),
        });
        UiHelpers.Fill(_grid, new[] { "نام کاربری", "نام کامل", "نقش", "شعبه", "وضعیت", "تاریخ ایجاد (شمسی)" }, rows);
    }

    private UserRole SelectedRole() =>
        _role.SelectedItem is ComboItem item && Enum.TryParse<UserRole>(item.Value, out var role) ? role : UserRole.Cashier;

    private async Task CreateAsync()
    {
        try
        {
            var role = SelectedRole();
            var branchId = role == UserRole.Cashier ? UiHelpers.RequiredBranchId(_branch) : (int?)null;
            await _services.Users.CreateUserAsync(
                _user,
                _username.Text.Trim(),
                _fullName.Text.Trim(),
                _password.Text,
                role,
                branchId,
                DateTime.Now);

            _username.Clear();
            _fullName.Clear();
            _password.Clear();
            UiHelpers.ShowInfo(this, "کاربر جدید ایجاد شد.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
