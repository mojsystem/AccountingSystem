using System.Data;
using AccountingSystem.Core.Common;

namespace AccountingSystem.WinForms;

internal interface IRefreshable
{
    Task RefreshAsync();
}

/// <summary>آیتم ComboBox با مقدار فنی و متن نمایشی.</summary>
internal sealed class ComboItem
{
    public ComboItem(string value, string text)
    {
        Value = value;
        Text = text;
    }

    public string Value { get; }

    public string Text { get; }

    public override string ToString() => Text;
}

internal static class UiHelpers
{
    public static void ShowError(IWin32Window owner, Exception ex)
    {
        var message = ex is BusinessRuleException or ConcurrencyConflictException
            ? ex.Message
            : "خطای غیرمنتظره: " + ex.Message;
        MessageBox.Show(owner, message, "خطا", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    public static void ShowInfo(IWin32Window owner, string message) =>
        MessageBox.Show(owner, message, "پیام", MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = SystemColors.Window,
    };

    public static void Fill(DataGridView grid, string[] headers, IEnumerable<string[]> rows)
    {
        var table = new DataTable();
        for (var i = 0; i < headers.Length; i++)
        {
            table.Columns.Add("c" + i, typeof(string));
        }
        foreach (var row in rows)
        {
            table.Rows.Add(row);
        }

        grid.DataSource = table;
        for (var i = 0; i < headers.Length && i < grid.Columns.Count; i++)
        {
            grid.Columns[i].HeaderText = headers[i];
        }
    }

    public static Label MakeLabel(string text, Point? location = null)
    {
        var label = new Label { Text = text, AutoSize = true, Margin = new Padding(6, 10, 2, 0) };
        if (location.HasValue)
        {
            label.Location = location.Value;
        }
        return label;
    }

    public static FlowLayoutPanel CreateInputPanel() => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(8),
        WrapContents = true,
    };

    /// <summary>انتخاب آیتمی که مقدار آن برابر value است؛ در غیر این صورت اولین آیتم.</summary>
    public static void SelectByValue(ComboBox box, string? value)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboItem item && item.Value == value)
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }
}
