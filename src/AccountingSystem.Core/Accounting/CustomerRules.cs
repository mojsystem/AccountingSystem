using System.Text;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Accounting;

/// <summary>
/// اعتبارسنجی و تمیز کردن اطلاعات مشتری. ارقام فارسی و عربی به لاتین تبدیل می‌شوند تا جست‌وجو و یکتایی درست کار کنند.
/// کد مشتری را سیستم می‌سازد (ستون محاسباتی پایگاه داده) و این کلاس آن را نمی‌خواند و تغییر نمی‌دهد.
/// </summary>
public static class CustomerRules
{
    public const int MaxNameLength = 100;

    public const int MinNationalCodeLength = 5;

    public const int MaxNationalCodeLength = 20;

    public const int MaxCityLength = 60;

    public const int MaxTextLength = 250;

    public const int MinAccountNumberLength = 5;

    public const int MaxAccountNumberLength = 34;

    public const int CardNumberLength = 16;

    public static CustomerInput Clean(CustomerInput input)
    {
        var name = (input.FullName ?? string.Empty).Trim();
        if (name.Length < 2)
        {
            throw new BusinessRuleException("نام و نام خانوادگی مشتری را وارد کنید (حداقل ۲ نویسه).");
        }
        if (name.Length > MaxNameLength)
        {
            throw new BusinessRuleException($"نام مشتری نمی‌تواند بیش از {MaxNameLength} کاراکتر باشد.");
        }

        var nationalCode = Optional(NormalizeDigits(input.NationalCode), "کد ملی", MaxNationalCodeLength);
        if (nationalCode is not null)
        {
            if (nationalCode.Length < MinNationalCodeLength)
            {
                throw new BusinessRuleException($"کد ملی یا شناسه‌ی مشتری باید حداقل {MinNationalCodeLength} نویسه باشد.");
            }
            foreach (var ch in nationalCode)
            {
                if (!char.IsAsciiLetterOrDigit(ch))
                {
                    throw new BusinessRuleException("کد ملی یا شناسه فقط می‌تواند شامل حروف لاتین و عدد باشد.");
                }
            }
        }

        return new CustomerInput(
            name,
            nationalCode,
            PhoneNumber(input.Phone, "تلفن ثابت", minDigits: 5),
            Optional(input.Address, "نشانی", MaxTextLength),
            Optional(input.Note, "یادداشت", MaxTextLength),
            PhoneNumber(input.Mobile, "شماره‌ی موبایل", minDigits: 10),
            Optional(input.City, "شهر", MaxCityLength),
            AccountNumber(input.AccountNumber),
            CardNumber(input.CardNumber));
    }

    /// <summary>متن جست‌وجو را مثل ذخیره‌ها تمیز می‌کند (ارقام فارسی لاتین می‌شوند).</summary>
    public static string NormalizeSearch(string? search) => NormalizeDigits(search) ?? string.Empty;

    /// <summary>
    /// شماره‌ی کارت برای فهرست‌ها: فقط چهار رقم آخر دیده می‌شود. شماره‌ی کامل فقط در فرم ویرایش نشان داده می‌شود.
    /// </summary>
    public static string? MaskCardNumber(string? cardNumber) =>
        string.IsNullOrEmpty(cardNumber) || cardNumber.Length < 4
            ? null
            : "•••• •••• •••• " + cardNumber[^4..];

    /// <summary>
    /// تلفن یا موبایل: عدد، فاصله، خط تیره و پرانتز پذیرفته می‌شود و در ذخیره فقط رقم‌ها (و + اول، برای کد کشور) می‌ماند.
    /// </summary>
    private static string? PhoneNumber(string? value, string label, int minDigits)
    {
        var normalized = NormalizeDigits(value);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (ch is ' ' or '-' or '(' or ')')
            {
                continue;
            }
            if (ch == '+' && builder.Length == 0)
            {
                builder.Append(ch);
                continue;
            }
            if (char.IsAsciiDigit(ch))
            {
                builder.Append(ch);
                continue;
            }
            throw new BusinessRuleException($"{label} فقط می‌تواند شامل عدد، + در ابتدا، فاصله یا خط تیره باشد.");
        }

        var digits = builder.ToString().Count(char.IsAsciiDigit);
        if (digits < minDigits || digits > 15)
        {
            throw new BusinessRuleException($"{label} باید بین {minDigits} تا ۱۵ رقم باشد.");
        }
        return builder.ToString();
    }

    /// <summary>شماره حساب یا شبا: حروف لاتین و عدد، بدون فاصله، با حروف بزرگ.</summary>
    private static string? AccountNumber(string? value)
    {
        var normalized = NormalizeDigits(value);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var compact = RemoveSeparators(normalized).ToUpperInvariant();
        if (compact.Length < MinAccountNumberLength || compact.Length > MaxAccountNumberLength)
        {
            throw new BusinessRuleException(
                $"شماره حساب باید بین {MinAccountNumberLength} تا {MaxAccountNumberLength} نویسه باشد (شماره‌ی شبا با IR شروع می‌شود).");
        }
        if (!compact.All(char.IsAsciiLetterOrDigit))
        {
            throw new BusinessRuleException("شماره حساب فقط می‌تواند شامل حروف لاتین و عدد باشد.");
        }
        return compact;
    }

    /// <summary>شماره کارت: دقیقاً ۱۶ رقم؛ فاصله و خط تیره پذیرفته می‌شوند و در ذخیره حذف می‌شوند.</summary>
    private static string? CardNumber(string? value)
    {
        var normalized = NormalizeDigits(value);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var compact = RemoveSeparators(normalized);
        if (compact.Length != CardNumberLength || !compact.All(char.IsAsciiDigit))
        {
            throw new BusinessRuleException($"شماره کارت باید {CardNumberLength} رقم باشد (فاصله و خط تیره مجازند).");
        }
        return compact;
    }

    private static string RemoveSeparators(string value) =>
        new(value.Where(ch => ch is not (' ' or '-')).ToArray());

    private static string? Optional(string? value, string label, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        if (trimmed.Length > maxLength)
        {
            throw new BusinessRuleException($"{label} نمی‌تواند بیش از {maxLength} کاراکتر باشد.");
        }
        return trimmed;
    }

    private static string? NormalizeDigits(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var chars = value.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (ch is >= '\u06F0' and <= '\u06F9')
            {
                chars[i] = (char)('0' + (ch - '\u06F0'));
            }
            else if (ch is >= '\u0660' and <= '\u0669')
            {
                chars[i] = (char)('0' + (ch - '\u0660'));
            }
        }
        return new string(chars);
    }
}
