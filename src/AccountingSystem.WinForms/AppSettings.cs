using System.Text.Json;

namespace AccountingSystem.WinForms;

/// <summary>خواندن رشته‌ی اتصال از appsettings.json کنار فایل اجرایی.</summary>
internal static class AppSettings
{
    public static string LoadConnectionString()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("فایل appsettings.json کنار برنامه یافت نشد.", path);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.TryGetProperty("ConnectionStrings", out var section)
            && section.TryGetProperty("AccountingSystem", out var value)
            && value.GetString() is { Length: > 0 } connectionString)
        {
            return connectionString;
        }

        throw new InvalidOperationException("رشته‌ی اتصال «AccountingSystem» در appsettings.json تنظیم نشده است.");
    }
}
