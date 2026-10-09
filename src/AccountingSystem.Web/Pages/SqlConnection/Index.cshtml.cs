using System.ComponentModel.DataAnnotations;
using AccountingSystem.Core.Common;
using AccountingSystem.Data.Schema;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;

namespace AccountingSystem.Web.Pages.SqlConnection;

/// <summary>راه‌اندازی SQL Server برای اولین اجرا یا زمانی که اتصال ذخیره‌شده در دسترس نیست.</summary>
public sealed class IndexModel : PageModel
{
    private readonly SqlConnectionRuntime _runtime;
    private readonly IConfiguration _configuration;

    public IndexModel(SqlConnectionRuntime runtime, IConfiguration configuration)
    {
        _runtime = runtime;
        _configuration = configuration;
    }

    [BindProperty]
    public SqlSetupInput Input { get; set; } = new();

    public IReadOnlyList<string> DiscoveredServers { get; private set; } = Array.Empty<string>();

    public string? ConnectionError => _runtime.SetupError;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!_runtime.SetupRequired)
        {
            return RedirectToPage("/Account/Login");
        }

        await LoadServersAsync(ct);
        if (string.IsNullOrWhiteSpace(Input.Server))
        {
            try
            {
                var saved = SqlConnectionProfileStore.Load();
                Input.Server = saved?.Server ?? DiscoveredServers.FirstOrDefault() ?? "localhost";
                Input.UserName = saved?.UserName ?? string.Empty;
            }
            catch (Exception)
            {
                Input.Server = DiscoveredServers.FirstOrDefault() ?? "localhost";
            }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!_runtime.SetupRequired)
        {
            return RedirectToPage("/Account/Login");
        }

        if (!ModelState.IsValid)
        {
            await LoadServersAsync(ct);
            return Page();
        }

        try
        {
            var profile = new SqlConnectionProfile(
                Input.Server.Trim(),
                Input.UserName.Trim(),
                Input.Password,
                "master");
            var masterConnection = profile.BuildConnectionString("master");

            await SqlDatabaseBootstrapper.TestServerConnectionAsync(masterConnection, ct);

            var backupFolder = _configuration["Backup:Folder"];
            var backupBeforeUpgrade = _configuration.GetValue("Backup:BeforeUpgrade", true);
            var result = await SqlDatabaseBootstrapper.EnsureApplicationDatabaseAsync(
                masterConnection,
                preferredDatabase: null,
                appBasePath: AppContext.BaseDirectory,
                upgradeOptions: new SchemaUpgradeOptions(
                    BackupBeforeUpgrade: backupBeforeUpgrade,
                    BackupFolder: backupFolder,
                    DatabaseFilesFolder: Path.Combine(AppContext.BaseDirectory, "database")),
                log: message => Console.WriteLine(message),
                ct: ct);

            // رمز در Windows با DPAPI محافظت می‌شود؛ پروفایل فقط بعد از اتصال، ساخت/ارتقای بانک ذخیره می‌شود.
            SqlConnectionProfileStore.Save(profile with { DatabaseName = result.DatabaseName });
            _runtime.Configure(result.ConnectionString);

            TempData["SqlSetupSuccess"] =
                $"اتصال برقرار شد. بانک «{result.DatabaseName}» با نسخه‌ی {result.Upgrade.Version} آماده است.";
            return RedirectToPage("/Account/Setup");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ModelState.AddModelError(string.Empty, FriendlyError(ex));
            await LoadServersAsync(ct);
            return Page();
        }
    }

    private async Task LoadServersAsync(CancellationToken ct)
    {
        DiscoveredServers = await SqlServerDiscovery.DiscoverAsync(ct);
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        SqlException => "اتصال یا آماده‌سازی بانک ناموفق بود. نام سرور/نمونه، نام کاربری و رمز SQL و مجوز ساخت بانک را بررسی کنید. " + ex.Message,
        UnauthorizedAccessException or IOException => "پوشه‌ی database کنار برنامه قابل استفاده نیست. برنامه و سرویس SQL Server باید اجازه‌ی دسترسی به آن را داشته باشند. " + ex.Message,
        _ => ex.Message,
    };
}

public sealed class SqlSetupInput
{
    [Required(ErrorMessage = "نام یا آدرس SQL Server را انتخاب یا وارد کنید.")]
    public string Server { get; set; } = string.Empty;

    [Required(ErrorMessage = "نام کاربری SQL را وارد کنید.")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "رمز SQL را وارد کنید.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;
}
