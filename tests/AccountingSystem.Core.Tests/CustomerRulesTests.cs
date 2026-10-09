using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using Xunit;

namespace AccountingSystem.Core.Tests;

public class CustomerRulesTests
{
    [Fact]
    public void Name_is_trimmed_and_optional_fields_become_null_when_empty()
    {
        var clean = CustomerRules.Clean(new CustomerInput("  زهرا احمدی  ", "   ", " ", "", null, " ", "", "  ", ""));

        Assert.Equal("زهرا احمدی", clean.FullName);
        Assert.Null(clean.NationalCode);
        Assert.Null(clean.Phone);
        Assert.Null(clean.Address);
        Assert.Null(clean.Note);
        Assert.Null(clean.Mobile);
        Assert.Null(clean.City);
        Assert.Null(clean.AccountNumber);
        Assert.Null(clean.CardNumber);
    }

    [Fact]
    public void Persian_and_arabic_digits_are_normalised_to_latin_digits()
    {
        var clean = CustomerRules.Clean(new CustomerInput("علی", "۰۰۱۲۳۴۵۶۷۸", "۰۲۱ ۱۲۳۴۵۶۷۸", null, null, "۰۹۱۲ ۳۴۵ ۶۷۸۹"));

        Assert.Equal("0012345678", clean.NationalCode);
        Assert.Equal("02112345678", clean.Phone);
        Assert.Equal("09123456789", clean.Mobile);
        var arabic = CustomerRules.Clean(new CustomerInput("علی", "٠٠١٢٣٤٥٦٧٨", null, null, null));
        Assert.Equal("0012345678", arabic.NationalCode);
    }

    [Fact]
    public void Name_needs_at_least_two_characters()
    {
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput(" ا ", null, null, null, null)));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput(null, null, null, null, null)));
    }

    [Fact]
    public void National_code_must_be_long_enough_and_alphanumeric()
    {
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", "1234", null, null, null)));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", "12345-6", null, null, null)));
        var passport = CustomerRules.Clean(new CustomerInput("John Smith", "P1234567", null, null, null));
        Assert.Equal("P1234567", passport.NationalCode);
    }

    [Fact]
    public void Phone_keeps_a_leading_plus_and_drops_separators()
    {
        Assert.Equal("+49911123", CustomerRules.Clean(new CustomerInput("علی", null, "+49 911 123", null, null)).Phone);
        Assert.Equal("02112345678", CustomerRules.Clean(new CustomerInput("علی", null, "(021) 1234-5678", null, null)).Phone);
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", null, "0912abc", null, null)));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", null, "09+12345678", null, null)));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", null, "1234", null, null)));
    }

    [Fact]
    public void Mobile_needs_at_least_ten_digits_and_accepts_country_code()
    {
        Assert.Equal("09121234567", CustomerRules.Clean(new CustomerInput("علی", null, null, null, null, "0912-123-4567")).Mobile);
        Assert.Equal("+989121234567", CustomerRules.Clean(new CustomerInput("علی", null, null, null, null, "+98 912 123 4567")).Mobile);
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", null, null, null, null, "0912")));
    }

    [Fact]
    public void Card_number_is_sixteen_digits_with_optional_separators()
    {
        var card = CustomerRules.Clean(new CustomerInput("علی", null, null, null, null, null, null, null, "6037-9975 1234 5678"));
        Assert.Equal("6037997512345678", card.CardNumber);

        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(
            new CustomerInput("علی", null, null, null, null, null, null, null, "603799751234567")));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(
            new CustomerInput("علی", null, null, null, null, null, null, null, "60379975123456ab")));
    }

    [Fact]
    public void Account_number_is_compacted_and_upper_cased()
    {
        var account = CustomerRules.Clean(new CustomerInput("علی", null, null, null, null, null, null, "ir12 3456 7890 1234 5678 9012 34"));
        Assert.Equal("IR123456789012345678901234", account.AccountNumber);

        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", null, null, null, null, null, null, "12")));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", null, null, null, null, null, null, "IR#123456")));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(
            new CustomerInput("علی", null, null, null, null, null, null, new string('1', 35))));
    }

    [Fact]
    public void Text_fields_have_length_limits()
    {
        var longNote = new string('ی', CustomerRules.MaxTextLength + 1);
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", null, null, null, longNote)));
        var longName = new string('ی', CustomerRules.MaxNameLength + 1);
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput(longName, null, null, null, null)));
        var longCity = new string('ی', CustomerRules.MaxCityLength + 1);
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", null, null, null, null, null, longCity)));
    }

    [Fact]
    public void Search_text_is_normalised_like_stored_values()
    {
        Assert.Equal("0912", CustomerRules.NormalizeSearch("  ۰۹۱۲ "));
        Assert.Equal(string.Empty, CustomerRules.NormalizeSearch(null));
    }

    [Fact]
    public void Card_number_in_lists_shows_only_the_last_four_digits()
    {
        Assert.Equal("•••• •••• •••• 5678", CustomerRules.MaskCardNumber("6037997512345678"));
        Assert.Null(CustomerRules.MaskCardNumber(null));
    }
}
