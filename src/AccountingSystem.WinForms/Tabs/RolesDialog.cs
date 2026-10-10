using System.Globalization;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>
/// نقش‌های یک شعبه: ایجاد نقش، تغییر وظیفه‌های نقش و حذف نقش بلااستفاده. فقط مدیر سیستم.
/// </summary>
internal sealed class RolesDialog : Form
{
    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly List<Permission> _items = Enum.GetValues<Permission>().ToList();
    private readonly ComboBox _branch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly ListBox _roles = new() { Width = 240, Height = 280, IntegralHeight = false };
    private readonly TextBox _name = new() { Width = 240 };
    private readonly CheckedListBox _permissions = new() { CheckOnClick = true, Width = 300, Height = 280, IntegralHeight = false };
    private readonly Button _create = new() { Text = "ثبت نقش جدید", AutoSize = true };
    private readonly Button _save = new() { Text = "ذخیره‌ی وظیفه‌های نقش انتخابی", AutoSize = true };
    private readonly Button _delete = new() { Text = "حذف نقش انتخابی", AutoSize = true };
    private IReadOnlyList<AccessRoleInfo> _roleList = Array.Empty<AccessRoleInfo>();

    public RolesDialog(AppServices services, CurrentUser user, IReadOnlyList<BranchInfo> branches)
    {
        _services = services;
        _user = user;
        Text = "نقش‌های شعبه";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(660, 440);

        foreach (var permission in _items)
        {
            _permissions.Items.Add(PermissionCodes.DisplayName(permission));
        }

        UiHelpers.FillBranches(_branch, branches, user, includeAll: false, selectedValue: null);

        var top = UiHelpers.CreateInputPanel();
        top.Controls.AddRange(new Control[] { UiHelpers.MakeLabel("شعبه:"), _branch });

        var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(8) };
        left.Controls.AddRange(new Control[] { UiHelpers.MakeLabel("نقش‌های این شعبه:"), _roles });

        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(8) };
        right.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("نام نقش جدید (۲ تا ۶۰ نویسه):"), _name,
            UiHelpers.MakeLabel("وظیفه‌های این نقش:"), _permissions,
            _create, _save, _delete,
        });

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoScroll = true };
        body.Controls.Add(right);
        body.Controls.Add(left);

        var close = new Button { Text = "بستن", DialogResult = DialogResult.OK, AutoSize = true };
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        bottom.Controls.Add(close);
        CancelButton = close;

        Controls.Add(body);
        Controls.Add(bottom);
        Controls.Add(top);

        _branch.SelectedIndexChanged += async (_, _) => await LoadRolesAsync();
        _roles.SelectedIndexChanged += (_, _) => ShowSelectedRole();
        _create.Click += async (_, _) => await CreateAsync();
        _save.Click += async (_, _) => await SaveAsync();
        _delete.Click += async (_, _) => await DeleteAsync();
        Shown += async (_, _) => await LoadRolesAsync();
        Theme.Apply(this);
    }

    private async Task LoadRolesAsync()
    {
        try
        {
            var branchId = UiHelpers.SelectedBranchId(_branch);
            _roleList = branchId is { } id
                ? await _services.Permissions.GetRolesAsync(_user, id)
                : Array.Empty<AccessRoleInfo>();

            _roles.Items.Clear();
            foreach (var role in _roleList)
            {
                _roles.Items.Add($"{role.Name} ({role.AssignedUsers.ToString(CultureInfo.InvariantCulture)} کاربر)");
            }
            ShowSelectedRole();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private void ShowSelectedRole()
    {
        var selected = SelectedRole();
        for (var i = 0; i < _items.Count; i++)
        {
            _permissions.SetItemChecked(i, selected is not null && selected.Permissions.Contains(_items[i]));
        }
        _save.Enabled = selected is not null;
        _delete.Enabled = selected is { AssignedUsers: 0 };
    }

    private AccessRoleInfo? SelectedRole() =>
        _roles.SelectedIndex >= 0 && _roles.SelectedIndex < _roleList.Count ? _roleList[_roles.SelectedIndex] : null;

    private List<Permission> CheckedPermissions() =>
        _items.Where((_, i) => _permissions.GetItemChecked(i)).ToList();

    private async Task CreateAsync()
    {
        try
        {
            var branchId = UiHelpers.RequiredBranchId(_branch);
            await _services.Permissions.CreateRoleAsync(_user, branchId, _name.Text.Trim(), CheckedPermissions(), DateTime.Now);
            _name.Clear();
            UiHelpers.ShowInfo(this, "نقش جدید ثبت شد.");
            await LoadRolesAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var role = SelectedRole() ?? throw new BusinessRuleException("یک نقش را از فهرست انتخاب کنید.");
            await _services.Permissions.SetRolePermissionsAsync(_user, role.Id, CheckedPermissions(), DateTime.Now);
            UiHelpers.ShowInfo(this, "وظیفه‌های نقش به‌روز شد و برای همه‌ی کاربران این نقش همان لحظه اعمال می‌شود.");
            await LoadRolesAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            var role = SelectedRole() ?? throw new BusinessRuleException("یک نقش را از فهرست انتخاب کنید.");
            var answer = MessageBox.Show(this, $"نقش «{role.Name}» حذف شود؟", "تأیید حذف", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                return;
            }
            await _services.Permissions.DeleteRoleAsync(_user, role.Id, DateTime.Now);
            await LoadRolesAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
