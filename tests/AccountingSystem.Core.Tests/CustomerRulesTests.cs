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
        var clean = CustomerRules.Clean(new CustomerInput("  زهرا احمدی  ", "   ", " ", "", null));

        Assert.Equal("زهرا احمدی", clean.FullName);
        Assert.Null(clean.NationalCode);
        Assert.Null(clean.Phone);
        Assert.Null(clean.Address);
        Assert.Null(clean.Note);
    }

    [Fact]
    public void Persian_and_arabic_digits_are_normalised_to_latin_digits()
    {
        var clean = CustomerRules.Clean(new CustomerInput("علی", "۰۰۱۲۳۴۵۶۷۸", "۰۹۱۲ ۳۴۵ ۶۷۸۹", null, null));

        Assert.Equal("0012345678", clean.NationalCode);
        Assert.Equal("0912 345 6789", clean.Phone);
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
    public void Phone_accepts_only_digits_and_common_separators()
    {
        Assert.Equal("+49 911 123", CustomerRules.Clean(new CustomerInput("علی", null, "+49 911 123", null, null)).Phone);
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", null, "0912abc", null, null)));
    }

    [Fact]
    public void Text_fields_have_length_limits()
    {
        var longNote = new string('ی', CustomerRules.MaxTextLength + 1);
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput("علی", null, null, null, longNote)));
        var longName = new string('ی', CustomerRules.MaxNameLength + 1);
        Assert.Throws<BusinessRuleException>(() => CustomerRules.Clean(new CustomerInput(longName, null, null, null, null)));
    }
}
