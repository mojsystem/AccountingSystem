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

    /// <summary>شبا: «IR» و ۲۴ رقم، یعنی ۲۶ نویسه.</summary>
    public const int ShebaLength = 26;

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

        var sheba1 = Sheba(input.Sheba1, "شبا ۱");
        var sheba2 = Sheba(input.Sheba2, "شبا ۲");
        RejectSameValue(sheba1, sheba2, "شماره‌ی شبا ۱ و ۲ نمی‌تواند یکسان باشد.");
        var card1 = CardNumber(input.CardNumber1, "شماره‌ی کارت ۱");
        var card2 = CardNumber(input.CardNumber2, "شماره‌ی کارت ۲");
        RejectSameValue(card1, card2, "شماره‌ی کارت ۱ و ۲ نمی‌تواند یکسان باشد.");

        return new CustomerInput(
            name,
            nationalCode,
            PhoneNumber(input.Phone, "تلفن ثابت", minDigits: 5),
            Optional(input.Address, "نشانی", MaxTextLength),
            Optional(input.Note, "یادداشت", MaxTextLength),
            PhoneNumber(input.Mobile, "شماره‌ی موبایل", minDigits: 10),
            Optional(input.City, "شهر", MaxCityLength),
            sheba1,
            sheba2,
            card1,
            card2);
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
    /// دو کارت یک مشتری برای فهرست: هر کدام با چهار رقم آخر و جدا با «/». اگر کارتی ثبت نشده باشد null است.
    /// </summary>
    public static string? MaskCardNumbers(string? first, string? second)
    {
        var parts = new[] { MaskCardNumber(first), MaskCardNumber(second) }
            .Where(part => !string.IsNullOrEmpty(part))
            .ToArray();
        return parts.Length == 0 ? null : string.Join(" / ", parts);
    }

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

    /// <summary>
    /// شبا: «IR» و ۲۴ رقم. فاصله، خط تیره و حروف کوچک پذیرفته می‌شوند و در ذخیره یکدست می‌شوند.
    /// اگر فقط ۲۴ رقم وارد شود، IR به ابتدای آن اضافه می‌شود.
    /// </summary>
    private static string? Sheba(string? value, string label)
    {
        var normalized = NormalizeDigits(value);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var compact = RemoveSeparators(normalized).ToUpperInvariant();
        if (compact.Length == ShebaLength - 2 && compact.All(char.IsAsciiDigit))
        {
            compact = "IR" + compact;
        }
        if (compact.Length != ShebaLength
            || !compact.StartsWith("IR", StringComparison.Ordinal)
            || !compact[2..].All(char.IsAsciiDigit))
        {
            throw new BusinessRuleException($"{label} باید با IR شروع شود و ۲۴ رقم بعد از آن داشته باشد (فاصله مجاز است).");
        }
        return compact;
    }

    /// <summary>شماره‌ی کارت: دقیقاً ۱۶ رقم؛ فاصله و خط تیره پذیرفته می‌شوند و در ذخیره حذف می‌شوند.</summary>
    private static string? CardNumber(string? value, string label)
    {
        var normalized = NormalizeDigits(value);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var compact = RemoveSeparators(normalized);
        if (compact.Length != CardNumberLength || !compact.All(char.IsAsciiDigit))
        {
            throw new BusinessRuleException($"{label} باید {CardNumberLength} رقم باشد (فاصله و خط تیره مجازند).");
        }
        return compact;
    }

    private static void RejectSameValue(string? first, string? second, string message)
    {
        if (first is not null && first == second)
        {
            throw new BusinessRuleException(message);
        }
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
