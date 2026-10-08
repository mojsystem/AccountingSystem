using System.Globalization;
using AccountingSystem.Data;
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
            var services = new AppServices(new SqlAccountingRepository(connectionString));

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
