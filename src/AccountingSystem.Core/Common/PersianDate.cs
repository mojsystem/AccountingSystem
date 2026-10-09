using System.Globalization;
using System.Text.RegularExpressions;

namespace AccountingSystem.Core.Common;

/// <summary>
/// نمایش و ورود تاریخ به تقویم شمسی. در پایگاه داده همیشه تاریخ میلادی ذخیره می‌شود.
/// </summary>
public static class PersianDate
{
    private static readonly PersianCalendar Calendar = new();
    private static readonly Regex DatePattern = new(@"^(\d{4})[/\-.](\d{1,2})[/\-.](\d{1,2})$", RegexOptions.CultureInvariant);

    public static string FormatDate(DateTime value) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0000}/{1:00}/{2:00}",
            Calendar.GetYear(value), Calendar.GetMonth(value), Calendar.GetDayOfMonth(value));

    public static string FormatDateTime(DateTime value) =>
        string.Format(CultureInfo.InvariantCulture, "{0} {1:HH:mm}", FormatDate(value), value);

    /// <summary>
    /// تاریخ شمسی ورودی کاربر مانند «۱۴۰۵/۰۷/۱۷» یا «1405-07-17» را به تاریخ میلادی (ساعت ۰۰:۰۰) تبدیل می‌کند.
    /// ارقام فارسی و عربی پذیرفته می‌شوند. روزهای نامعتبر (مثلاً ۳۱ مهر یا ۳۰ اسفند در سال غیرکبیسه) رد می‌شوند.
    /// </summary>
    public static bool TryParseDate(string? text, out DateTime date)
    {
        date = default;
        var normalized = InputParser.NormalizeDigits((text ?? string.Empty).Trim());
        var match = DatePattern.Match(normalized);
        if (!match.Success)
        {
            return false;
        }

        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (year < 1300 || year > 1600 || month < 1 || month > 12 || day < 1)
        {
            return false;
        }

        var maxDay = month <= 6 ? 31 : month <= 11 ? 30 : Calendar.IsLeapYear(year) ? 30 : 29;
        if (day > maxDay)
        {
            return false;
        }

        try
        {
            date = Calendar.ToDateTime(year, month, day, 0, 0, 0, 0);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// بازه‌ی تاریخ شمسی را به بازه‌ی میلادی [از، تا) تبدیل می‌کند. «از» و «تا» خالی باشند یعنی امروز.
    /// اگر «تا» قبل از «از» باشد، جای آن‌ها عوض می‌شود.
    /// </summary>
    public static bool TryParseRange(string? fromText, string? toText, DateTime today, out DateTime fromInclusive, out DateTime toExclusive, out string? error)
    {
        var from = today.Date;
        var to = today.Date;
        fromInclusive = from;
        toExclusive = to.AddDays(1);
        error = null;

        if (!string.IsNullOrWhiteSpace(fromText) && !TryParseDate(fromText, out from))
        {
            error = "تاریخ «از» را به‌صورت شمسی وارد کنید، مثلاً ۱۴۰۵/۰۷/۰۱.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(toText) && !TryParseDate(toText, out to))
        {
            error = "تاریخ «تا» را به‌صورت شمسی وارد کنید، مثلاً ۱۴۰۵/۰۷/۳۰.";
            return false;
        }

        if (to < from)
        {
            (from, to) = (to, from);
        }
        fromInclusive = from;
        toExclusive = to.AddDays(1);
        return true;
    }
}
