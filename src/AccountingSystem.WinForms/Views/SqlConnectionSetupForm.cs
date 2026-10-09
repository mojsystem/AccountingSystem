using AccountingSystem.Core.Common;
using AccountingSystem.Data.Schema;
using Microsoft.Data.SqlClient;

namespace AccountingSystem.WinForms.Views;

/// <summary>راه‌انداز اتصال SQL Server: انتخاب موتور کشف‌شده یا نوشتن دستی، ورود SQL Login، ساخت/ارتقای بانک.</summary>
internal sealed class SqlConnectionSetupForm : Form
{
    private readonly ComboBox _server = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems,
        RightToLeft = RightToLeft.No,
    };
    private readonly TextBox _userName = new() { Dock = DockStyle.Fill, MaxLength = 128, RightToLeft = RightToLeft.No };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 256, RightToLeft = RightToLeft.No };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = false, ForeColor = Color.FromArgb(185, 28, 28) };
    private readonly Button _connect = new() { Text = "آزمایش اتصال و آماده‌سازی بانک", AutoSize = true };
    private readonly Button _cancel = new() { Text = "انصراف", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly string? _preferredServer;
    private readonly bool _backupBeforeUpgrade;
    private readonly string? _backupFolder;

    public SqlConnectionSetupForm(
        string? preferredServer,
        string? savedUserName,
        string? savedPassword,
        bool backupBeforeUpgrade,
        string? backupFolder)
    {
        _preferredServer = preferredServer;
        _backupBeforeUpgrade = backupBeforeUpgrade;
        _backupFolder = backupFolder;
        _userName.Text = savedUserName ?? string.Empty;
        _password.Text = savedPassword ?? string.Empty;

        Text = "راه‌اندازی اتصال SQL Server";
        ClientSize = new Size(700, 430);
        MinimumSize = new Size(600, 400);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = Theme.BodyFont;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;

        var title = new Label
        {
            Text = "اتصال به SQL Server",
            Dock = DockStyle.Fill,
            Font = Theme.BoldFont,
            Padding = new Padding(4, 6, 4, 0),
        };
        var description = new Label
        {
            Text = "اتصال فعلی برقرار نشد. یک موتور شناسایی‌شده را انتخاب یا آدرس را دستی وارد کنید، سپس نام کاربری و رمز SQL را بنویسید. بانک موجود استفاده می‌شود؛ اگر نباشد، mojdb1، mojdb2 و ... ساخته می‌شود.",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Padding = new Padding(4, 4, 4, 6),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 7,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);
        layout.Controls.Add(description, 0, 1);
        layout.SetColumnSpan(description, 2);
        AddRow(layout, 2, "سرور / نمونه SQL:", _server);
        AddRow(layout, 3, "نام کاربری SQL:", _userName);
        AddRow(layout, 4, "رمز SQL:", _password);
        layout.Controls.Add(_status, 0, 5);
        layout.SetColumnSpan(_status, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        buttons.Controls.AddRange(new Control[] { _connect, _cancel });
        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);

        AcceptButton = _connect;
        CancelButton = _cancel;
        _connect.Click += async (_, _) => await ConnectAsync();
        Shown += async (_, _) => await LoadServersAsync();
        Theme.Apply(this);
    }

    public string? ConnectionString { get; private set; }

    public SchemaUpgradeResult? Upgrade { get; private set; }

    public SqlConnectionProfile? Profile { get; private set; }

    public bool DatabaseCreated { get; private set; }

    private static void AddRow(TableLayoutPanel panel, int row, string caption, Control control)
    {
        panel.Controls.Add(new Label { Text = caption, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight }, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private async Task LoadServersAsync()
    {
        try
        {
            _status.Text = "در حال جست‌وجوی SQL Serverهای قابل‌شناسایی...";
            var servers = await SqlServerDiscovery.DiscoverAsync();
            var suggestions = new List<string>(servers);
            foreach (var fallback in new[] { "localhost", ".", @"localhost\SQLEXPRESS", @".\SQLEXPRESS" })
            {
                if (!suggestions.Contains(fallback, StringComparer.OrdinalIgnoreCase))
                {
                    suggestions.Add(fallback);
                }
            }

            _server.Items.Clear();
            _server.Items.AddRange(suggestions.Cast<object>().ToArray());
            _server.Text = servers.FirstOrDefault(s => string.Equals(s, _preferredServer, StringComparison.OrdinalIgnoreCase))
                ?? servers.FirstOrDefault()
                ?? _preferredServer
                ?? "localhost";
            _status.Text = servers.Count == 0
                ? "SQL Browser سروری معرفی نکرد؛ آدرس را می‌توانید دستی وارد کنید."
                : $"{servers.Count} موتور SQL Server شناسایی شد. اگر موتور موردنظر نیست، آدرسش را دستی بنویسید.";
        }
        catch (Exception ex)
        {
            _status.Text = "جست‌وجوی خودکار انجام نشد؛ آدرس SQL Server را دستی بنویسید. " + ex.Message;
            _server.Text = _preferredServer ?? "localhost";
        }
    }

    private async Task ConnectAsync()
    {
        var server = _server.Text.Trim();
        var userName = _userName.Text.Trim();
        if (server.Length == 0 || userName.Length == 0 || _password.Text.Length == 0)
        {
            _status.Text = "سرور، نام کاربری SQL و رمز را وارد کنید.";
            return;
        }

        SetBusy(true);
        try
        {
            var profile = new SqlConnectionProfile(server, userName, _password.Text, "master");
            var masterConnection = profile.BuildConnectionString("master");
            _status.ForeColor = Theme.MutedText;
            _status.Text = "در حال آزمایش اتصال و آماده‌سازی پایگاه داده...";

            await SqlDatabaseBootstrapper.TestServerConnectionAsync(masterConnection);
            var result = await SqlDatabaseBootstrapper.EnsureApplicationDatabaseAsync(
                masterConnection,
                preferredDatabase: null,
                appBasePath: AppContext.BaseDirectory,
                upgradeOptions: new SchemaUpgradeOptions(
                    BackupBeforeUpgrade: _backupBeforeUpgrade,
                    BackupFolder: _backupFolder,
                    DatabaseFilesFolder: Path.Combine(AppContext.BaseDirectory, "database")),
                log: message => _status.Text = message);

            var savedProfile = profile with { DatabaseName = result.DatabaseName };
            SqlConnectionProfileStore.Save(savedProfile);
            Profile = savedProfile;
            ConnectionString = result.ConnectionString;
            Upgrade = result.Upgrade;
            DatabaseCreated = result.Created;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.ForeColor = Color.FromArgb(185, 28, 28);
            _status.Text = ex is SqlException
                ? "اتصال/ساخت بانک ناموفق بود. سرور، نام کاربری، رمز و مجوز dbcreator/sysadmin را بررسی کنید. " + ex.Message
                : ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _connect.Enabled = !busy;
        _cancel.Enabled = !busy;
        _server.Enabled = !busy;
        _userName.Enabled = !busy;
        _password.Enabled = !busy;
        UseWaitCursor = busy;
    }
}
