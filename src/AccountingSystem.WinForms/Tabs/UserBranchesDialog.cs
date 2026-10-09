using System.Globalization;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>
/// عضویت یک کاربر در شعبه‌ها: برای هر شعبه یک نقش یا «بدون دسترسی»، و یک شعبه‌ی اصلی (باید عضو باشد).
/// </summary>
internal sealed class UserBranchesDialog : Form
{
    private readonly List<(int BranchId, ComboBox Role, RadioButton Default)> _rows = new();

    public UserBranchesDialog(
        string username,
        IReadOnlyList<BranchInfo> branches,
        IReadOnlyDictionary<int, IReadOnlyList<AccessRoleInfo>> rolesByBranch,
        UserAccess? current)
    {
        Text = "شعبه‌ها و نقش‌های " + username;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Top, Padding = new Padding(10) };
        table.Controls.Add(UiHelpers.MakeLabel("شعبه"), 0, 0);
        table.Controls.Add(UiHelpers.MakeLabel("نقش در این شعبه"), 1, 0);
        table.Controls.Add(UiHelpers.MakeLabel("شعبه‌ی اصلی"), 2, 0);

        var row = 1;
        foreach (var branch in branches)
        {
            var roleBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            roleBox.Items.Add(new ComboItem(string.Empty, "— بدون دسترسی —"));
            var roles = rolesByBranch.TryGetValue(branch.Id, out var list) ? list : Array.Empty<AccessRoleInfo>();
            foreach (var role in roles)
            {
                roleBox.Items.Add(new ComboItem(role.Id.ToString(CultureInfo.InvariantCulture), role.Name));
            }

            var currentRole = current is not null && current.Branches.TryGetValue(branch.Id, out var inBranch) ? inBranch.RoleId : (int?)null;
            UiHelpers.SelectByValue(roleBox, currentRole?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            if (roleBox.SelectedIndex < 0)
            {
                roleBox.SelectedIndex = 0;
            }

            var defaultButton = new RadioButton
            {
                Text = "اصلی",
                AutoSize = true,
                Checked = current?.DefaultBranchId == branch.Id,
            };

            table.Controls.Add(UiHelpers.MakeLabel(branch.Code + " - " + branch.Name), 0, row);
            table.Controls.Add(roleBox, 1, row);
            table.Controls.Add(defaultButton, 2, row);
            _rows.Add((branch.Id, roleBox, defaultButton));
            row++;
        }

        var ok = new Button { Text = "ذخیره", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "انصراف", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(table);
        Controls.Add(buttons);
    }

    /// <summary>نقش انتخاب‌شده برای هر شعبه؛ null یعنی بدون دسترسی در آن شعبه.</summary>
    public IReadOnlyDictionary<int, int?> RoleByBranch =>
        _rows.ToDictionary(r => r.BranchId, r => SelectedId(r.Role));

    /// <summary>شعبه‌ی اصلی انتخاب‌شده (باید یکی از شعبه‌های با نقش باشد).</summary>
    public int? DefaultBranchId =>
        _rows.Where(r => r.Default.Checked).Select(r => (int?)r.BranchId).FirstOrDefault();

    private static int? SelectedId(ComboBox box) =>
        box.SelectedItem is ComboItem item
        && int.TryParse(item.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}
