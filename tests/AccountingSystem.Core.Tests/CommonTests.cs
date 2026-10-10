using AccountingSystem.Core.Common;
using AccountingSystem.Core.Security;
using Xunit;

namespace AccountingSystem.Core.Tests;

public class CommonTests
{
    [Fact]
    public void RoundIrr_rounds_half_away_from_zero()
    {
        Assert.Equal(3m, MoneyMath.RoundIrr(2.5m));
        Assert.Equal(2m, MoneyMath.RoundIrr(2.4m));
        Assert.Equal(-3m, MoneyMath.RoundIrr(-2.5m));
    }

    [Fact]
    public void Password_hash_verifies_only_the_original_password()
    {
        var hash = PasswordHashing.Hash("Secret#123");

        Assert.True(PasswordHashing.Verify("Secret#123", hash));
        Assert.False(PasswordHashing.Verify("Secret#124", hash));
        Assert.NotEqual(hash, PasswordHashing.Hash("Secret#123"));
    }

    [Fact]
    public void Password_verification_returns_false_for_malformed_hash()
    {
        Assert.False(PasswordHashing.Verify("x", "not-a-hash"));
        Assert.False(PasswordHashing.Verify("x", "PBKDF2$SHA256$abc$!!$!!"));
    }

    [Fact]
    public void Input_parser_accepts_persian_digits_and_separators()
    {
        Assert.True(InputParser.TryParseDecimal("۱,۲۵۰,۰۰۰", out var value));
        Assert.Equal(1_250_000m, value);

        Assert.True(InputParser.TryParseDecimal("۱۲۳٫۵", out var fraction));
        Assert.Equal(123.5m, fraction);

        Assert.False(InputParser.TryParseDecimal("abc", out _));
        Assert.False(InputParser.TryParseDecimal("", out _));
    }

    [Fact]
    public void Persian_date_uses_solar_calendar()
    {
        // ۱ فروردین ۱۴۰۰ برابر ۲۱ مارس ۲۰۲۱ است.
        Assert.Equal("1400/01/01 10:30", PersianDate.FormatDateTime(new DateTime(2021, 3, 21, 10, 30, 0)));
    }

    [Fact]
    public void Persian_date_input_converts_to_gregorian_midnight()
    {
        Assert.True(PersianDate.TryParseDate("1400/01/01", out var nowruz));
        Assert.Equal(new DateTime(2021, 3, 21), nowruz);

        Assert.True(PersianDate.TryParseDate("۱۴۰۰/۰۷/۰۱", out var mehr));
        Assert.Equal(new DateTime(2021, 9, 23), mehr);

        Assert.True(PersianDate.TryParseDate("1400-07-01", out var dashed));
        Assert.Equal(mehr, dashed);
    }

    [Fact]
    public void Persian_date_input_rejects_invalid_dates()
    {
        Assert.False(PersianDate.TryParseDate("1400/13/01", out _));
        Assert.False(PersianDate.TryParseDate("1400/07/31", out _));
        Assert.False(PersianDate.TryParseDate("1400/00/10", out _));
        Assert.False(PersianDate.TryParseDate("2021-03-21", out _));
        Assert.False(PersianDate.TryParseDate("abc", out _));
        Assert.False(PersianDate.TryParseDate("", out _));
        Assert.False(PersianDate.TryParseDate(null, out _));
    }

    [Fact]
    public void Persian_date_round_trips_through_formatting()
    {
        Assert.True(PersianDate.TryParseDate("1400/07/01", out var date));
        Assert.Equal("1400/07/01", PersianDate.FormatDate(date));
    }

    [Fact]
    public void Persian_date_range_treats_empty_bounds_as_today_and_swaps_reversed_bounds()
    {
        var today = new DateTime(2021, 10, 1, 15, 0, 0);

        Assert.True(PersianDate.TryParseRange(null, "", today, out var fromToday, out var toToday, out var error));
        Assert.Null(error);
        Assert.Equal(new DateTime(2021, 10, 1), fromToday);
        Assert.Equal(new DateTime(2021, 10, 2), toToday);

        Assert.True(PersianDate.TryParseRange("1400/07/10", "1400/07/01", today, out var from, out var to, out _));
        Assert.Equal(new DateTime(2021, 9, 23), from);
        Assert.Equal(new DateTime(2021, 10, 3), to);
    }

    [Fact]
    public void Persian_date_range_reports_an_invalid_bound()
    {
        var ok = PersianDate.TryParseRange("1400/99/01", null, new DateTime(2021, 10, 1), out _, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void Normalize_digits_keeps_other_characters()
    {
        Assert.Equal("1405/07/17 - x", InputParser.NormalizeDigits("۱۴۰۵/۰۷/۱۷ - x"));
        Assert.Equal("123", InputParser.NormalizeDigits("١٢٣"));
    }
}
