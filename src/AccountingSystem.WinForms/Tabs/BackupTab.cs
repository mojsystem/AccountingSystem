using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>پشتیبان‌گیری و بازیابی پایگاه داده. فقط مدیر سیستم این تب را می‌بیند.</summary>
internal sealed class BackupTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        Padding = new Padding(12, 10, 12, 4),
        AutoSize = false,
    };
    private readonly Button _create = new() { Text = "ساخت پشتیبان الان", AutoSize = true };
    private readonly Button _restore = new() { Text = "بازیابی پشتیبان انتخاب‌شده", AutoSize = true };
    private readonly Button _refresh = new() { Text = "بروزرسانی فهرست", AutoSize = true };
    private readonly DataGridView _backups = UiHelpers.CreateGrid();
    private readonly DataGridView _history = UiHelpers.CreateGrid();
    private IReadOnlyList<BackupInfo> _backupRows = Array.Empty<BackupInfo>();

    public BackupTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        var buttons = UiHelpers.CreateInputPanel();
        buttons.Controls.AddRange(new Control[] { _create, _restore, _refresh });

        var historyTitle = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 0),
            Text = "سابقه‌ی نسخه‌های پایگاه داده (از جدول SchemaVersion)",
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        layout.Controls.Add(_backups, 0, 2);
        layout.Controls.Add(historyTitle, 0, 3);
        layout.Controls.Add(_history, 0, 4);
        Controls.Add(layout);

        _create.Click += async (_, _) => await CreateAsync();
        _restore.Click += async (_, _) => await RestoreAsync();
        _refresh.Click += async (_, _) => await RefreshSafeAsync();
    }

    public async Task RefreshAsync()
    {
        var status = await _services.Backup.GetStatusAsync(_user);
        var versionText = status.DatabaseVersion is { } version
            ? $"{version} از {status.ApplicationVersion}"
            : "نسخه‌بندی نشده (پیش از این نسخه؛ با اولین اجرا ثبت می‌شود)";
        _status.Text =
            $"پایگاه داده: {status.DatabaseName}    نسخه‌ی پایگاه داده: {versionText}\r\n" +
            $"پوشه‌ی پشتیبان روی سرور: {status.BackupFolder}\r\n" +
            "پیش از هر ارتقا و پیش از هر بازیابی، به‌صورت خودکار پشتیبان گرفته می‌شود.";

        _backupRows = await _services.Backup.ListBackupsAsync(_user);
        var backupRows = _backupRows.Select(b => new[]
        {
            PersianDate.FormatDateTime(b.FinishedAt),
            b.DatabaseName,
            b.FilePath,
            b.SizeBytes > 0 ? (b.SizeBytes / 1048576.0).ToString("N1") : "-",
        });
        UiHelpers.Fill(_backups, new[] { "زمان (شمسی)", "بانک", "فایل پشتیبان روی سرور", "حجم (MB)" }, backupRows);

        var historyRows = status.History.Select(h => new[]
        {
            h.Version.ToString(),
            h.Name,
            PersianDate.FormatDateTime(h.AppliedAt),
            h.AppliedBy,
        });
        UiHelpers.Fill(_history, new[] { "نسخه", "نام", "اجرا در (شمسی)", "اجرا کننده" }, historyRows);
    }

    private async Task RefreshSafeAsync()
    {
        try
        {
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
            var backup = await _services.Backup.CreateBackupAsync(_user, DateTime.Now);
            UiHelpers.ShowInfo(this, "پشتیبان ساخته شد:\n" + backup.FilePath);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private async Task RestoreAsync()
    {
        var row = _backups.CurrentRow;
        if (row is null || row.Index < 0 || row.Index >= _backupRows.Count)
        {
            UiHelpers.ShowInfo(this, "ابتدا یک پشتیبان را از فهرست انتخاب کنید.");
            return;
        }

        var backup = _backupRows[row.Index];
        var answer = MessageBox.Show(
            this,
            "بازیابی این پشتیبان همه‌ی اطلاعات فعلی را با نسخه‌ی پشتیبان جایگزین می‌کند." +
            "\n\nپیش از آن یک پشتیبان از وضعیت فعلی گرفته می‌شود. در این مدت همه‌ی کاربران قطع می‌شوند." +
            "\n\nفایل: " + backup.FilePath +
            "\n\nادامه می‌دهید؟",
            "تأیید بازیابی",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            await _services.Backup.RestoreAsync(_user, backup.FilePath, DateTime.Now);
            MessageBox.Show(
                this,
                "بازیابی انجام شد. برنامه بسته می‌شود؛ دوباره اجرا کنید.",
                "بازیابی",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            Application.Exit();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
