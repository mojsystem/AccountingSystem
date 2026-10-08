using System.Globalization;

namespace AccountingSystem.Core.Common;

/// <summary>نمایش تاریخ به تقویم شمسی. در پایگاه داده همیشه تاریخ میلادی ذخیره می‌شود.</summary>
public static class PersianDate
{
    private static readonly PersianCalendar Calendar = new();

    public static string FormatDate(DateTime value) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0000}/{1:00}/{2:00}",
            Calendar.GetYear(value), Calendar.GetMonth(value), Calendar.GetDayOfMonth(value));

    public static string FormatDateTime(DateTime value) =>
        string.Format(CultureInfo.InvariantCulture, "{0} {1:HH:mm}", FormatDate(value), value);
}
