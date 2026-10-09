using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Accounting;

/// <summary>
/// اعتبارسنجی اطلاعات مشتری. ارقام فارسی و عربی به لاتین تبدیل می‌شوند تا کد ملی و تلفن یکسان ذخیره شوند.
/// </summary>
public static class CustomerRules
{
    public const int MaxNameLength = 100;

    public const int MinNationalCodeLength = 5;

    public const int MaxNationalCodeLength = 20;

    public const int MaxPhoneLength = 20;

    public const int MaxTextLength = 250;

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

        var phone = Optional(NormalizeDigits(input.Phone), "تلفن", MaxPhoneLength);
        if (phone is not null)
        {
            foreach (var ch in phone)
            {
                if (!char.IsAsciiDigit(ch) && ch is not ('+' or '-' or ' ' or '(' or ')'))
                {
                    throw new BusinessRuleException("شماره تلفن فقط می‌تواند شامل عدد، + ، - ، فاصله و پرانتز باشد.");
                }
            }
        }

        return new CustomerInput(
            name,
            nationalCode,
            phone,
            Optional(input.Address, "نشانی", MaxTextLength),
            Optional(input.Note, "یادداشت", MaxTextLength));
    }

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
