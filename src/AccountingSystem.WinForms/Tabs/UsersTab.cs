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
    private readonly Button _permissionsButton = new() { Text = "دسترسی ویرایش و ابطال…", AutoSize = true };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<BranchInfo> _branches = Array.Empty<BranchInfo>();
    private IReadOnlyList<UserInfo> _users = Array.Empty<UserInfo>();

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
            _create, _permissionsButton,
        });
        inputs.Visible = user.Role == UserRole.Admin;

        Controls.Add(_grid);
        Controls.Add(inputs);

        _create.Click += async (_, _) => await CreateAsync();
        _permissionsButton.Click += async (_, _) => await EditPermissionsAsync();
    }

    public async Task RefreshAsync()
    {
        _branches = await _services.Branches.GetBranchesAsync();
        var previous = (_branch.SelectedItem as ComboItem)?.Value;
        UiHelpers.FillBranches(_branch, _branches, _user, includeAll: false, selectedValue: previous);
        _branch.Enabled = SelectedRole() == UserRole.Cashier;

        var users = await _services.Users.GetUsersAsync(_user);
        _users = users;
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

    /// <summary>دسترسی‌های ویرایش و ابطال کاربر انتخاب‌شده را تنظیم می‌کند (فقط برای کاربر صندوق).</summary>
    private async Task EditPermissionsAsync()
    {
        try
        {
            var index = _grid.CurrentRow?.Index ?? -1;
            var target = index >= 0 && index < _users.Count
                ? _users[index]
                : throw new BusinessRuleException("یک کاربر را از جدول انتخاب کنید.");
            if (target.Role == UserRole.Admin)
            {
                throw new BusinessRuleException("مدیر به همه‌ی دسترسی‌ها دسترسی دارد و نیازی به تنظیم ندارد.");
            }

            var assignments = await _services.Permissions.GetAssignmentsAsync(_user);
            IReadOnlySet<Permission> current = assignments.TryGetValue(target.Id, out var set) ? set : new HashSet<Permission>();
            using var dialog = new PermissionsDialog(target.Username, current);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            await _services.Permissions.SetPermissionsAsync(_user, target.Id, dialog.Selected, DateTime.Now);
            UiHelpers.ShowInfo(this, "دسترسی‌های کاربر به‌روز شد و همان لحظه اعمال می‌شود.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

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
