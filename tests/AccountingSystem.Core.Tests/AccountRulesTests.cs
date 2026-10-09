using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using Xunit;

namespace AccountingSystem.Core.Tests;

public class AccountRulesTests
{
    private static AccountInfo Group(string code, string type = "Expense") =>
        new(code, "گروه " + code, type, 1, null, false, true, true, false);

    private static AccountInfo Account(string code, int level, string parent, string type,
        bool system = false, bool active = true, bool children = false, bool postings = false) =>
        new(code, "حساب " + code, type, level, parent, system, active, children, postings);

    [Fact]
    public void Group_code_must_be_a_single_digit()
    {
        Assert.Equal(1, AccountRules.ValidateNew("1", null, "Asset"));
        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateNew("10", null, "Asset"));
        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateNew("0", null, "Asset"));
    }

    [Fact]
    public void Child_level_is_parent_level_plus_one_and_code_must_extend_parent()
    {
        var kull = Account("60", 2, "6", "Expense");

        Assert.Equal(3, AccountRules.ValidateNew("6004", kull, "Expense"));
        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateNew("7004", kull, "Expense"));
        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateNew("60", kull, "Expense"));
    }

    [Fact]
    public void Structure_stops_at_four_levels()
    {
        var detail = Account("1101-XY", 4, "1101", "Asset");

        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateNew("1101-XY-1", detail, "Asset"));
    }

    [Fact]
    public void Child_type_must_match_the_parent_type()
    {
        var kull = Account("60", 2, "6", "Expense");

        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateNew("6004", kull, "Asset"));
    }

    [Fact]
    public void System_accounts_cannot_get_children()
    {
        var cash = Account("1001", 3, "10", "Asset", system: true, postings: true);

        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateNew("10011", cash, "Asset"));
    }

    [Fact]
    public void System_account_keeps_code_parent_type_and_active_state()
    {
        var cash = Account("1001", 3, "10", "Asset", system: true, postings: true);
        var parent = Account("10", 2, "1", "Asset");

        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateChange(cash, "1002", "Asset", "10", parent, true));
        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateChange(cash, "1001", "Asset", "11", parent, true));
        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateChange(cash, "1001", "Expense", "10", parent, true));
        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateChange(cash, "1001", "Asset", "10", parent, false));
        Assert.Equal(3, AccountRules.ValidateChange(cash, "1001", "Asset", "10", parent, true));
    }

    [Fact]
    public void Account_with_children_cannot_be_deactivated_or_moved()
    {
        var kull = Account("60", 2, "6", "Expense", children: true);
        var group = Group("6");

        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateChange(kull, "60", "Expense", "6", group, false));
        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateChange(kull, "61", "Expense", "6", group, true));
        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateChange(kull, "60", "Asset", "6", group, true));
        Assert.Equal(2, AccountRules.ValidateChange(kull, "60", "Expense", "6", group, true));
    }

    [Fact]
    public void Account_with_postings_keeps_its_code_but_can_be_deactivated()
    {
        var leaf = Account("6001", 3, "60", "Expense", postings: true);
        var kull = Account("60", 2, "6", "Expense", children: true);

        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateChange(leaf, "6009", "Expense", "60", kull, true));
        Assert.Equal(3, AccountRules.ValidateChange(leaf, "6001", "Expense", "60", kull, false));
    }

    [Fact]
    public void Only_active_leaf_accounts_are_postable()
    {
        Assert.False(Group("6").IsPostable);
        Assert.False(Account("60", 2, "6", "Expense", children: true).IsPostable);
        Assert.True(Account("6001", 3, "60", "Expense").IsPostable);
        Assert.False(Account("6002", 3, "60", "Expense", active: false).IsPostable);
    }

    [Fact]
    public void Delete_is_refused_for_system_parents_and_posted_accounts()
    {
        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateDelete(Account("1001", 3, "10", "Asset", system: true)));
        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateDelete(Account("60", 2, "6", "Expense", children: true)));
        Assert.Throws<BusinessRuleException>(() => AccountRules.ValidateDelete(Account("6001", 3, "60", "Expense", postings: true)));
        AccountRules.ValidateDelete(Account("6009", 3, "60", "Expense"));
    }

    [Fact]
    public void Code_accepts_latin_letters_digits_and_hyphen_only()
    {
        Assert.Equal("1101-USD", AccountRules.CleanCode(" 1101-USD "));
        Assert.Throws<BusinessRuleException>(() => AccountRules.CleanCode("۱۱۰۱"));
        Assert.Throws<BusinessRuleException>(() => AccountRules.CleanCode("12 3"));
        Assert.Throws<BusinessRuleException>(() => AccountRules.CleanCode(new string('1', 21)));
    }
}
