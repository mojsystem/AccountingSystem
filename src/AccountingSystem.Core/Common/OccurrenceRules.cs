namespace AccountingSystem.Core.Common;

/// <summary>
/// قاعده‌ی زمان وقوع سند. سندها با تاریخ گذشته تا MaxBackdateDays روز قبل مجاز هستند.
/// اگر تاریخ داده نشود، زمان فعلی سرور ثبت می‌شود. برای تاریخ گذشته، ساعت همان لحظه‌ی ثبت است.
/// </summary>
public static class OccurrenceRules
{
    public const int MaxBackdateDays = 30;

    public static DateTime Resolve(DateTime? requestedDate, DateTime now)
    {
        var moment = Truncate(now);
        if (requestedDate is null)
        {
            return moment;
        }

        var day = requestedDate.Value.Date;
        if (day > now.Date)
        {
            throw new BusinessRuleException("تاریخ سند نمی‌تواند از امروز جلوتر باشد.");
        }
        if (day < now.Date.AddDays(-MaxBackdateDays))
        {
            throw new BusinessRuleException($"ثبت سند با تاریخ بیش از {MaxBackdateDays} روز قبل مجاز نیست.");
        }

        return day + moment.TimeOfDay;
    }

    /// <summary>ستون‌های زمان در دیتابیس دقت ثانیه دارند؛ کسر ثانیه حذف می‌شود تا مقایسه‌ها ثابت بمانند.</summary>
    public static DateTime Truncate(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
}
