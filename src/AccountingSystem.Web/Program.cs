using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Services;
using System.Net;
using AccountingSystem.Data;
using AccountingSystem.Data.Schema;
using AccountingSystem.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.SqlClient;

// appsettings.json و wwwroot کنار فایل اجرایی هستند؛ مسیر را به پوشه‌ی جاری وابسته نمی‌کنیم
// تا برنامه از هر مسیری اجرا شود (مثلاً `dotnet AccountingSystem.Web.dll` از ریشه‌ی مخزن).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

var backupFolder = builder.Configuration["Backup:Folder"];
var backupBeforeUpgrade = builder.Configuration.GetValue("Backup:BeforeUpgrade", true);
var appFilesFolder = Path.Combine(AppContext.BaseDirectory, "database");

// متغیر محیطی (در CI و استقرار) اولویت دارد؛ سپس پروفایل ذخیره‌شده‌ی DPAPI، و در آخر appsettings.json.
var environmentConnection = Environment.GetEnvironmentVariable("ConnectionStrings__AccountingSystem");
var configuredConnection = builder.Configuration.GetConnectionString("AccountingSystem");
SqlConnectionProfile? savedProfile = null;
string? profileLoadError = null;
try
{
    savedProfile = SqlConnectionProfileStore.Load();
}
catch (Exception ex)
{
    profileLoadError = "تنظیم اتصال ذخیره‌شده خوانده نشد: " + ex.Message;
}

var initialConnectionString = !string.IsNullOrWhiteSpace(environmentConnection)
    ? environmentConnection
    : savedProfile?.BuildConnectionString() ?? configuredConnection;
var preferredDatabase = savedProfile?.DatabaseName;
if (preferredDatabase is null && !string.IsNullOrWhiteSpace(initialConnectionString))
{
    try
    {
        preferredDatabase = new SqlConnectionStringBuilder(initialConnectionString).InitialCatalog;
    }
    catch (ArgumentException)
    {
        // صفحه‌ی راه‌اندازی اجازه می‌دهد رشته‌ی اتصال را از نو تنظیم کنیم.
    }
}

var sqlRuntime = new SqlConnectionRuntime(initialConnectionString, setupRequired: true, setupError: profileLoadError);
if (string.IsNullOrWhiteSpace(initialConnectionString))
{
    sqlRuntime.RequireSetup("رشته‌ی اتصال SQL Server تنظیم نشده است.");
}
else
{
    try
    {
        // هر بار ابتدا اتصال به خود موتور (master) آزمایش می‌شود؛ اگر موفق بود بانک برنامه انتخاب/ارتقا می‌شود.
        await SqlDatabaseBootstrapper.TestServerConnectionAsync(initialConnectionString);
        var bootstrap = await SqlDatabaseBootstrapper.EnsureApplicationDatabaseAsync(
            initialConnectionString,
            preferredDatabase: preferredDatabase,
            appBasePath: AppContext.BaseDirectory,
            upgradeOptions: new SchemaUpgradeOptions(
                BackupBeforeUpgrade: backupBeforeUpgrade,
                BackupFolder: backupFolder,
                DatabaseFilesFolder: appFilesFolder),
            log: message => Console.WriteLine(message));
        sqlRuntime.Configure(bootstrap.ConnectionString);

        if (savedProfile is not null && string.IsNullOrWhiteSpace(environmentConnection))
        {
            SqlConnectionProfileStore.Save(savedProfile with { DatabaseName = bootstrap.DatabaseName });
        }

        Console.WriteLine($"SQL Server متصل است؛ بانک «{bootstrap.DatabaseName}» در نسخه‌ی {bootstrap.Upgrade.Version} است.");
    }
    catch (SchemaUpgradeException)
    {
        // ساختار ناشناخته، checksum تغییرکرده یا شکست اجرای مهاجرت؛ نباید آن را با wizard پنهان کنیم.
        throw;
    }
    catch (Exception ex) when (ex is SqlException or ArgumentException or InvalidOperationException)
    {
        sqlRuntime.RequireSetup("اتصال SQL Server برقرار نشد یا بانک در دسترس نیست. " + ex.Message);
        Console.Error.WriteLine("برنامه در حالت راه‌اندازی SQL بالا می‌آید: " + ex.Message);
    }
}

builder.Services.AddSingleton(sqlRuntime);
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Setup");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/SqlConnection/Index");
    options.Conventions.AuthorizeFolder("/Users", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Branches", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Roles", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Accounts", "AdminOnly");
    options.Conventions.AuthorizeFolder("/BankAccounts", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Backup", "AdminOnly");
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "AccountingSystem.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

builder.Services.AddScoped<IAccountingRepository>(sp =>
    new SqlAccountingRepository(sp.GetRequiredService<SqlConnectionRuntime>().ConnectionString));
builder.Services.AddScoped<BranchService>();
builder.Services.AddScoped<CurrencyTradeService>();
builder.Services.AddScoped<CashTransactionService>();
builder.Services.AddScoped<CurrencyAdminService>();
builder.Services.AddScoped<BankAccountService>();
builder.Services.AddScoped<ManualJournalService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<ReceiptService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<IDatabaseMaintenance>(sp => new SqlDatabaseMaintenance(
    sp.GetRequiredService<SqlConnectionRuntime>().ConnectionString,
    backupFolder));
builder.Services.AddScoped<BackupService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.Use(async (context, next) =>
{
    var isSqlSetup = context.Request.Path.StartsWithSegments("/SqlConnection");
    if (sqlRuntime.SetupRequired && !isSqlSetup)
    {
        context.Response.Redirect("/SqlConnection");
        return;
    }

    // فرم دریافت رمز SQL فقط روی همین رایانه با HTTP قابل استفاده است؛ از شبکه، HTTPS الزامی است.
    if (sqlRuntime.SetupRequired && isSqlSetup && !context.Request.IsHttps
        && !IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("راه‌اندازی اتصال SQL از راه دور فقط از طریق HTTPS مجاز است.");
        return;
    }

    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
