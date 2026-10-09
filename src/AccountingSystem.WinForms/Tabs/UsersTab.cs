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
    private readonly ComboBox _role = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ComboBox _branch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly Button _create = new() { Text = "ایجاد کاربر", AutoSize = true };
    private readonly Button _branchesButton = new() { Text = "شعبه‌ها و نقش‌های کاربر…", AutoSize = true };
    private readonly Button _rolesButton = new() { Text = "نقش‌های شعبه…", AutoSize = true };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<BranchInfo> _branches = Array.Empty<BranchInfo>();
    private IReadOnlyList<UserInfo> _users = Array.Empty<UserInfo>();

    public UsersTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        _role.Items.Add(new ComboItem(nameof(UserRole.Cashier), "کاربر شعبه (یک شعبه‌ی اصلی)"));
        _role.Items.Add(new ComboItem(nameof(UserRole.Admin), "مدیر سیستم (همه‌ی شعبه‌ها)"));
        _role.SelectedIndex = 0;
        _role.SelectedIndexChanged += (_, _) => _branch.Enabled = SelectedRole() == UserRole.Cashier;

        var inputs = UiHelpers.CreateInputPanel();
        inputs.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("نام کاربری:"), _username,
            UiHelpers.MakeLabel("نام کامل:"), _fullName,
            UiHelpers.MakeLabel("گذرواژه:"), _password,
            UiHelpers.MakeLabel("نوع:"), _role,
            UiHelpers.MakeLabel("شعبه‌ی اصلی:"), _branch,
            _create,
        });

        var manage = UiHelpers.CreateInputPanel();
        manage.Controls.AddRange(new Control[] { _branchesButton, _rolesButton });

        var isAdmin = user.Role == UserRole.Admin;
        inputs.Visible = isAdmin;
        manage.Visible = isAdmin;

        Controls.Add(_grid);
        Controls.Add(manage);
        Controls.Add(inputs);

        _create.Click += async (_, _) => await CreateAsync();
        _branchesButton.Click += async (_, _) => await EditMembershipsAsync();
        _rolesButton.Click += (_, _) => OpenRoles();
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
            u.Role == UserRole.Admin ? "مدیر سیستم" : "کاربر شعبه",
            u.BranchName ?? "—",
            u.IsActive ? "فعال" : "غیرفعال",
            PersianDate.FormatDateTime(u.CreatedAt),
        });
        UiHelpers.Fill(_grid, new[] { "نام کاربری", "نام کامل", "نقش سیستم", "شعبه‌ی اصلی", "وضعیت", "تاریخ ایجاد (شمسی)" }, rows);
    }

    private UserRole SelectedRole() =>
        _role.SelectedItem is ComboItem item && Enum.TryParse<UserRole>(item.Value, out var role) ? role : UserRole.Cashier;

    private UserInfo SelectedUser()
    {
        var index = _grid.CurrentRow?.Index ?? -1;
        return index >= 0 && index < _users.Count
            ? _users[index]
            : throw new BusinessRuleException("یک کاربر را از جدول انتخاب کنید.");
    }

    /// <summary>عضویت کاربر در شعبه‌ها، نقش هر شعبه و شعبه‌ی اصلی. تغییرات همان لحظه اعمال می‌شوند.</summary>
    private async Task EditMembershipsAsync()
    {
        try
        {
            var target = SelectedUser();
            if (target.Role == UserRole.Admin)
            {
                throw new BusinessRuleException("مدیر سیستم به همه‌ی شعبه‌ها دسترسی دارد و نقش شعبه‌ای ندارد.");
            }

            var access = await _services.Permissions.GetAllAccessAsync(_user);
            UserAccess? current = access.TryGetValue(target.Id, out var found) ? found : null;

            var roles = new Dictionary<int, IReadOnlyList<AccessRoleInfo>>();
            foreach (var branch in _branches)
            {
                roles[branch.Id] = await _services.Permissions.GetRolesAsync(_user, branch.Id);
            }

            using var dialog = new UserBranchesDialog(target.Username, _branches, roles, current);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            await _services.Permissions.ApplyMembershipsAsync(_user, target.Id, dialog.RoleByBranch, dialog.DefaultBranchId, DateTime.Now);
            UiHelpers.ShowInfo(this, "شعبه‌ها و نقش‌های کاربر به‌روز شد و همان لحظه اعمال می‌شود.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private void OpenRoles()
    {
        try
        {
            using var dialog = new RolesDialog(_services, _user, _branches);
            dialog.ShowDialog(this);
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
            UiHelpers.ShowInfo(this, "کاربر جدید ایجاد شد. شعبه‌ها و نقش‌های او را با دکمه‌ی «شعبه‌ها و نقش‌های کاربر» تنظیم کنید.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
