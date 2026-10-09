using System.Globalization;
using AccountingSystem.Data;
using AccountingSystem.Data.Schema;
using AccountingSystem.WinForms.Views;
using Microsoft.Data.SqlClient;

namespace AccountingSystem.WinForms;

internal static class Program
{
    private const string DefaultConnectionString = "Server=localhost;Database=AccountingSystem;Trusted_Connection=True;TrustServerCertificate=True;";

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
            var connectionFromEnvironment = Environment.GetEnvironmentVariable("ConnectionStrings__AccountingSystem");
            string settingsConnection;
            if (!string.IsNullOrWhiteSpace(connectionFromEnvironment))
            {
                settingsConnection = connectionFromEnvironment;
            }
            else
            {
                try
                {
                    settingsConnection = AppSettings.LoadConnectionString();
                }
                catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
                {
                    // حتی اگر تنظیمات وجود نداشت یا JSON خراب بود، تلاش با نمونه‌ی پیش‌فرض انجام می‌شود؛
                    // اگر نشد، راه‌انداز گرافیکی SQL نمایش داده خواهد شد.
                    Console.Error.WriteLine("تنظیم اتصال خوانده نشد: " + ex.Message);
                    settingsConnection = DefaultConnectionString;
                }
            }

            SqlConnectionProfile? savedProfile = null;
            try
            {
                savedProfile = SqlConnectionProfileStore.Load();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("پروفایل ذخیره‌شده قابل خواندن نبود؛ اتصال دوباره پرسیده می‌شود: " + ex.Message);
            }

            var environmentOverridesSettings = !string.IsNullOrWhiteSpace(connectionFromEnvironment);
            var connectionString = environmentOverridesSettings
                ? settingsConnection
                : savedProfile?.BuildConnectionString() ?? settingsConnection;
            var preferredDatabase = !environmentOverridesSettings && savedProfile is not null
                ? savedProfile.DatabaseName
                : TryGetDatabase(connectionString) ?? "AccountingSystem";

            string? backupFolder = null;
            var backupBeforeUpgrade = true;
            try
            {
                (backupFolder, backupBeforeUpgrade) = AppSettings.LoadBackupOptions();
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
            {
                Console.Error.WriteLine("تنظیم پشتیبان خوانده نشد؛ مقدارهای پیش‌فرض استفاده می‌شوند: " + ex.Message);
            }

            var upgradeOptions = new SchemaUpgradeOptions(
                BackupBeforeUpgrade: backupBeforeUpgrade,
                BackupFolder: backupFolder,
                DatabaseFilesFolder: Path.Combine(AppContext.BaseDirectory, "database"));

            SqlDatabaseBootstrapResult bootstrap;
            try
            {
                // هر بار ابتدا خود موتور SQL (master) بررسی می‌شود، نه فقط بانک برنامه.
                SqlDatabaseBootstrapper.TestServerConnectionAsync(connectionString).GetAwaiter().GetResult();
                bootstrap = SqlDatabaseBootstrapper.EnsureApplicationDatabaseAsync(
                    connectionString,
                    preferredDatabase,
                    AppContext.BaseDirectory,
                    upgradeOptions,
                    message => Console.WriteLine(message)).GetAwaiter().GetResult();

                if (savedProfile is not null && !environmentOverridesSettings)
                {
                    SqlConnectionProfileStore.Save(savedProfile with { DatabaseName = bootstrap.DatabaseName });
                }
            }
            catch (Exception ex) when (ex is SqlException or ArgumentException or InvalidOperationException)
            {
                // اتصال قبلی در دسترس نیست: فهرست SQLهای کشف‌شده و ورود SQL Login نمایش داده می‌شود.
                using var setup = new SqlConnectionSetupForm(
                    TryGetServer(connectionString),
                    savedProfile?.UserName,
                    savedProfile?.Password,
                    backupBeforeUpgrade,
                    backupFolder);
                if (setup.ShowDialog() != DialogResult.OK || setup.ConnectionString is null || setup.Upgrade is null)
                {
                    return;
                }

                connectionString = setup.ConnectionString;
                bootstrap = new SqlDatabaseBootstrapResult(
                    setup.Profile!.DatabaseName,
                    setup.ConnectionString,
                    setup.DatabaseCreated,
                    setup.Upgrade);
            }

            if (bootstrap.Created || bootstrap.Upgrade.AppliedVersions.Count > 0)
            {
                var backupNote = bootstrap.Upgrade.BackupPath is null
                    ? string.Empty
                    : "\n\nپشتیبان قبل از ارتقا:\n" + bootstrap.Upgrade.BackupPath;
                MessageBox.Show(
                    $"پایگاه داده‌ی «{bootstrap.DatabaseName}» آماده است (نسخه‌ی {bootstrap.Upgrade.Version}).{backupNote}",
                    "راه‌اندازی پایگاه داده",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            var services = new AppServices(
                new SqlAccountingRepository(bootstrap.ConnectionString),
                new SqlDatabaseMaintenance(bootstrap.ConnectionString, backupFolder));

            using var login = new LoginForm(services);
            if (login.ShowDialog() != DialogResult.OK || login.SignedInUser is null)
            {
                return;
            }

            Application.Run(new MainForm(services, login.SignedInUser, bootstrap.Upgrade.Version));
        }
        catch (Exception ex)
        {
            MessageBox.Show("خطا در راه‌اندازی برنامه: " + ex.Message, "خطا", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string? TryGetServer(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString).DataSource;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? TryGetDatabase(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
