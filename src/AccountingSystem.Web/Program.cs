using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Services;
using AccountingSystem.Data;
using Microsoft.AspNetCore.Authentication.Cookies;

// appsettings.json و wwwroot کنار فایل اجرایی هستند؛ مسیر را به پوشه‌ی جاری وابسته نمی‌کنیم
// تا برنامه از هر مسیری اجرا شود (مثلاً `dotnet AccountingSystem.Web.dll` از ریشه‌ی مخزن).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

var connectionString = builder.Configuration.GetConnectionString("AccountingSystem")
    ?? throw new InvalidOperationException("رشته‌ی اتصال «AccountingSystem» در appsettings.json تعریف نشده است.");

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Setup");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AuthorizeFolder("/Users", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Branches", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Roles", "AdminOnly");
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

builder.Services.AddScoped<IAccountingRepository>(_ => new SqlAccountingRepository(connectionString));
builder.Services.AddScoped<BranchService>();
builder.Services.AddScoped<CurrencyTradeService>();
builder.Services.AddScoped<CurrencyAdminService>();
builder.Services.AddScoped<ManualJournalService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<ReceiptService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<PermissionService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
