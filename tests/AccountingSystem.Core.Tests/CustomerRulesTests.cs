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
        var clean = CustomerRules.Clean(new CustomerInput("  زهرا احمدی  ", NationalCode: "   ", Phone: " ", Address: "",
            Mobile: " ", City: "", Sheba1: "  ", Sheba2: "", CardNumber1: "", CardNumber2: "  "));

        Assert.Equal("زهرا احمدی", clean.FullName);
        Assert.Null(clean.NationalCode);
        Assert.Null(clean.Phone);
        Assert.Null(clean.Address);
        Assert.Null(clean.Note);
        Assert.Null(clean.Mobile);
        Assert.Null(clean.City);
        Assert.Null(clean.Sheba1);
        Assert.Null(clean.Sheba2);
        Assert.Null(clean.CardNumber1);
        Assert.Null(clean.CardNumber2);
    }

    [Fact]
    public void Persian_and_arabic_digits_are_normalised_to_latin_digits()
    {
        var clean = CustomerRules.Clean(new CustomerInput("علی", "۰۰۱۲۳۴۵۶۷۸", "۰۲۱ ۱۲۳۴۵۶۷۸", null, null, "۰۹۱۲ ۳۴۵ ۶۷۸۹"));

        Assert.Equal("0012345678", clean.NationalCode);
        Assert.Equal("02112345678", clean.Phone);
        Assert.Equal("09123456789", clean.Mobile);
        var arabic = CustomerRules.Clean(new CustomerInput("علی", "٠٠١٢٣٤٥٦٧٨"));
        Assert.Equal("0012345678", arabic.NationalCode);
    }

    [Fact]
    public void Name_needs_at_least_two_characters()
    {
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput(" ا ")));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput(null)));
    }

    [Fact]
    public void National_code_must_be_long_enough_and_alphanumeric()
    {
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", "1234")));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", "12345-6")));
        var passport = CustomerRules.Clean(new CustomerInput("John Smith", "P1234567"));
        Assert.Equal("P1234567", passport.NationalCode);
    }

    [Fact]
    public void Phone_keeps_a_leading_plus_and_drops_separators()
    {
        Assert.Equal("+49911123", CustomerRules.Clean(new CustomerInput("علی", Phone: "+49 911 123")).Phone);
        Assert.Equal("02112345678", CustomerRules.Clean(new CustomerInput("علی", Phone: "(021) 1234-5678")).Phone);
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", Phone: "0912abc")));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", Phone: "09+12345678")));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", Phone: "1234")));
    }

    [Fact]
    public void Mobile_needs_at_least_ten_digits_and_accepts_country_code()
    {
        Assert.Equal("09121234567", CustomerRules.Clean(new CustomerInput("علی", Mobile: "0912-123-4567")).Mobile);
        Assert.Equal("+989121234567", CustomerRules.Clean(new CustomerInput("علی", Mobile: "+98 912 123 4567")).Mobile);
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", Mobile: "0912")));
    }

    [Fact]
    public void There_are_two_card_numbers_of_sixteen_digits_with_optional_separators()
    {
        var cards = CustomerRules.Clean(new CustomerInput("علی",
            CardNumber1: "6037-9975 1234 5678", CardNumber2: "۵۸۹۲ ۱۰۱۲ ۳۴۵۶ ۷۸۹۰"));
        Assert.Equal("6037997512345678", cards.CardNumber1);
        Assert.Equal("5892101234567890", cards.CardNumber2);

        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", CardNumber1: "603799751234567")));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", CardNumber2: "60379975123456ab")));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(
            new CustomerInput("علی", CardNumber1: "6037997512345678", CardNumber2: "6037-9975-1234-5678")));
    }

    [Fact]
    public void There_are_two_sheba_numbers_compacted_and_upper_cased()
    {
        var sheba = CustomerRules.Clean(new CustomerInput("علی",
            Sheba1: "ir12 3456 7890 1234 5678 9012 34", Sheba2: "IR 1111 2222 3333 4444 5555 6666"));
        Assert.Equal("IR123456789012345678901234", sheba.Sheba1);
        Assert.Equal("IR111122223333444455556666", sheba.Sheba2);

        var withoutPrefix = CustomerRules.Clean(new CustomerInput("علی", Sheba1: "123456789012345678901234"));
        Assert.Equal("IR123456789012345678901234", withoutPrefix.Sheba1);

        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", Sheba1: "IR1234")));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", Sheba2: "DE123456789012345678901234")));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", Sheba1: "IR#123456789012345678901234")));
    }

    [Fact]
    public void The_two_sheba_numbers_and_the_two_cards_must_differ()
    {
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی",
            Sheba1: "IR123456789012345678901234", Sheba2: "ir12-3456-7890-1234-5678-9012-34")));
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی",
            CardNumber1: "6037997512345678", CardNumber2: "6037 9975 1234 5678")));
    }

    [Fact]
    public void Text_fields_have_length_limits()
    {
        var longNote = new string('ی', CustomerRules.MaxTextLength + 1);
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", Note: longNote)));
        var longName = new string('ی', CustomerRules.MaxNameLength + 1);
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput(longName)));
        var longCity = new string('ی', CustomerRules.MaxCityLength + 1);
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", City: longCity)));
    }

    [Fact]
    public void Search_text_is_normalised_like_stored_values()
    {
        Assert.Equal("0912", CustomerRules.NormalizeSearch("  ۰۹۱۲ "));
        Assert.Equal(string.Empty, CustomerRules.NormalizeSearch(null));
    }

    [Fact]
    public void Card_numbers_in_lists_show_only_the_last_four_digits()
    {
        Assert.Equal("•••• •••• •••• 5678", CustomerRules.MaskCardNumber("6037997512345678"));
        Assert.Null(CustomerRules.MaskCardNumber(null));
        Assert.Equal("•••• •••• •••• 5678 / •••• •••• •••• 7890",
            CustomerRules.MaskCardNumbers("6037997512345678", "5892101234567890"));
        Assert.Equal("•••• •••• •••• 5678", CustomerRules.MaskCardNumbers("6037997512345678", null));
        Assert.Null(CustomerRules.MaskCardNumbers(null, null));
    }
}
