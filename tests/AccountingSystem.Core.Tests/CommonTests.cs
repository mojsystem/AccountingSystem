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
}
