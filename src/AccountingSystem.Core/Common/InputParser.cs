using System.Globalization;
using System.Text;

namespace AccountingSystem.Core.Common;

/// <summary>
/// تبدیل ورودی کاربر به عدد. ارقام فارسی/عربی، جداکننده‌ی هزارگان و ممیز فارسی پشتیبانی می‌شوند.
/// </summary>
public static class InputParser
{
    public static bool TryParseDecimal(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.Trim())
        {
            switch (ch)
            {
                case >= '\u06F0' and <= '\u06F9':
                    builder.Append((char)('0' + (ch - '\u06F0')));
                    break;
                case >= '\u0660' and <= '\u0669':
                    builder.Append((char)('0' + (ch - '\u0660')));
                    break;
                case '\u066B':
                case '.':
                    builder.Append('.');
                    break;
                case ',':
                case '\u066C':
                case '\u200C':
                case ' ':
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return decimal.TryParse(builder.ToString(),
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out value);
    }
}
