namespace AccountingSystem.Core.Common;

/// <summary>خطای قواعد کسب‌وکار؛ متن این خطا مستقیماً برای کاربر نمایش داده می‌شود.</summary>
public sealed class BusinessRuleException : Exception
{
    public BusinessRuleException(string message) : base(message)
    {
    }
}

/// <summary>داده بین خواندن و ثبت تغییر کرده است (کنترل همزمانی خوش‌بینانه).</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException()
        : base("اطلاعات در همین لحظه توسط کاربر دیگری تغییر کرده است. لطفاً دوباره تلاش کنید.")
    {
    }
}
