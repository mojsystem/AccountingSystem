using System.Globalization;
using AccountingSystem.Core.Common;
using AccountingSystem.Data;
using AccountingSystem.Data.Schema;
using AccountingSystem.WinForms.Views;

namespace AccountingSystem.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            var connectionString = AppSettings.LoadConnectionString();
            var (backupFolder, backupBeforeUpgrade) = AppSettings.LoadBackupOptions();

            // ارتقای خودکار پایگاه داده پیش از ورود: نسخه‌ی بانک با نسخه‌ی برنامه مقایسه می‌شود
            // و نسخه‌های باقی‌مانده به ترتیب اجرا می‌شوند. اگر ارتقا شکست بخورد، برنامه شروع نمی‌شود.
            var upgrade = SchemaUpgrader.EnsureUpToDateAsync(
                connectionString,
                new SchemaUpgradeOptions(backupBeforeUpgrade, backupFolder)).GetAwaiter().GetResult();
            if (upgrade.AppliedVersions.Count > 0)
            {
                var backupNote = upgrade.BackupPath is null ? string.Empty : "\n\nپشتیبان قبل از ارتقا:\n" + upgrade.BackupPath;
                MessageBox.Show(
                    $"پایگاه داده به نسخه‌ی {upgrade.Version} ارتقا یافت.{backupNote}",
                    "ارتقای پایگاه داده",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            var services = new AppServices(
                new SqlAccountingRepository(connectionString),
                new SqlDatabaseMaintenance(connectionString, backupFolder));

            using var login = new LoginForm(services);
            if (login.ShowDialog() != DialogResult.OK || login.SignedInUser is null)
            {
                return;
            }

            Application.Run(new MainForm(services, login.SignedInUser));
        }
        catch (Exception ex)
        {
            MessageBox.Show("خطا در راه‌اندازی برنامه: " + ex.Message, "خطا", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
