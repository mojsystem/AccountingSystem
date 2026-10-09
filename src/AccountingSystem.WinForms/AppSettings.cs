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

        using var document = ParseFile(path);
        if (document.RootElement.TryGetProperty("ConnectionStrings", out var section)
            && section.TryGetProperty("AccountingSystem", out var value)
            && value.GetString() is { Length: > 0 } connectionString)
        {
            return connectionString;
        }

        throw new InvalidOperationException("رشته‌ی اتصال «AccountingSystem» در appsettings.json تنظیم نشده است.");
    }

    /// <summary>
    /// خواندن JSON. رایج‌ترین علت خطا یک بک‌اسلش تکی در رشته‌ی اتصال است (مثل localhost\MSSQLSERVER)،
    /// پس پیام خطا محل خطا و شکل درست نوشتن را هم نشان می‌دهد.
    /// </summary>
    private static JsonDocument ParseFile(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            // LineNumber و BytePositionInLine در System.Text.Json از صفر شروع می‌شوند؛ برای نمایش یک‌مبنا می‌کنیم.
            var line = (ex.LineNumber ?? 0) + 1;
            var column = (ex.BytePositionInLine ?? 0) + 1;
            throw new InvalidOperationException(
                $"فایل appsettings.json معتبر نیست (خط {line}، ستون {column})." + Environment.NewLine +
                @"در JSON بک‌اسلش (\) کاراکتر escape است؛ پس در رشته‌ی اتصال هر بک‌اسلش را دو بار بنویسید." + Environment.NewLine +
                @"مثلاً Server=localhost\\MSSQLSERVER به‌جای Server=localhost\MSSQLSERVER. برای نمونه‌ی پیش‌فرض فقط Server=localhost بنویسید." + Environment.NewLine +
                $"جزئیات فنی: {ex.Message}" + Environment.NewLine +
                $"مسیر فایل: {path}",
                ex);
        }
    }
}
