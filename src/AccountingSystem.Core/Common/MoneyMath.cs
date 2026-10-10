using System.Globalization;

namespace AccountingSystem.Core.Common;

public static class MoneyMath
{
    /// <summary>گرد کردن ریال به عدد صحیح (نیم به دور از صفر).</summary>
    public static decimal RoundIrr(decimal value) => Math.Round(value, 0, MidpointRounding.AwayFromZero);

    public static decimal RoundTo(decimal value, int decimals) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    public static string FormatAmount(decimal value, int decimals) =>
        value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public static string FormatRate(decimal value) =>
        value.ToString("#,0.####", CultureInfo.InvariantCulture);
}
