using System.Linq;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Xunit;

namespace AccountingSystem.Core.Tests;

public class PermissionRulesTests
{
    private static readonly UserAccess Admin = new(1, UserRole.Admin, true, null, new HashSet<Permission>());

    private static UserAccess Cashier(int branchId, bool isActive = true, params Permission[] permissions) =>
        new(7, UserRole.Cashier, isActive, branchId, new HashSet<Permission>(permissions));

    [Fact]
    public void Admin_is_allowed_everything_in_every_branch()
    {
        foreach (var permission in Enum.GetValues<Permission>())
        {
            Assert.True(PermissionRules.IsAllowed(Admin, permission, 1));
            Assert.True(PermissionRules.IsAllowed(Admin, permission, 99));
            PermissionRules.Require(Admin, permission, 99);
        }
    }

    [Fact]
    public void Cashier_without_permission_is_denied_and_the_message_names_the_permission()
    {
        var cashier = Cashier(1);

        Assert.False(PermissionRules.IsAllowed(cashier, Permission.TradeVoid, 1));
        var error = Assert.Throws<BusinessRuleException>(() => PermissionRules.Require(cashier, Permission.TradeVoid, 1));
        Assert.Contains(PermissionCodes.DisplayName(Permission.TradeVoid), error.Message);
    }

    [Fact]
    public void Granted_permission_applies_only_to_documents_of_the_users_own_branch()
    {
        var cashier = Cashier(1, true, Permission.TradeVoid);

        Assert.True(PermissionRules.IsAllowed(cashier, Permission.TradeVoid, 1));
        Assert.False(PermissionRules.IsAllowed(cashier, Permission.TradeVoid, 2));
        Assert.Throws<BusinessRuleException>(() => PermissionRules.Require(cashier, Permission.TradeVoid, 2));
    }

    [Fact]
    public void A_grant_does_not_imply_any_other_permission()
    {
        var cashier = Cashier(1, true, Permission.TradeEdit);

        Assert.False(PermissionRules.IsAllowed(cashier, Permission.TradeVoid, 1));
        Assert.False(PermissionRules.IsAllowed(cashier, Permission.OpeningEdit, 1));
        Assert.False(PermissionRules.IsAllowed(cashier, Permission.ManualVoid, 1));
    }

    [Fact]
    public void Inactive_users_are_denied_even_with_permissions_or_admin_role()
    {
        var inactiveCashier = Cashier(1, false, Permission.TradeVoid);
        var inactiveAdmin = Admin with { IsActive = false };

        Assert.False(PermissionRules.IsAllowed(inactiveCashier, Permission.TradeVoid, 1));
        Assert.Throws<BusinessRuleException>(() => PermissionRules.Require(inactiveCashier, Permission.TradeVoid, 1));
        Assert.False(PermissionRules.IsAllowed(inactiveAdmin, Permission.TradeVoid, 1));
    }

    [Fact]
    public void Admin_cannot_be_given_individual_permissions_but_cashiers_can()
    {
        Assert.Throws<BusinessRuleException>(() => PermissionRules.EnsureAssignable(Admin));
        PermissionRules.EnsureAssignable(Cashier(1));
    }

    [Fact]
    public void Permission_codes_round_trip_and_unknown_codes_are_rejected()
    {
        foreach (var permission in Enum.GetValues<Permission>())
        {
            Assert.True(PermissionCodes.TryParse(PermissionCodes.ToCode(permission), out var parsed));
            Assert.Equal(permission, parsed);
        }

        Assert.False(PermissionCodes.TryParse("TRADE_DELETE", out _));
        Assert.False(PermissionCodes.TryParse("trade_edit", out _));
        Assert.False(PermissionCodes.TryParse(null, out _));
    }

    [Fact]
    public void Every_permission_has_a_distinct_code_and_display_name()
    {
        var all = Enum.GetValues<Permission>();

        Assert.Equal(all.Length, all.Select(PermissionCodes.ToCode).Distinct().Count());
        Assert.Equal(all.Length, all.Select(PermissionCodes.DisplayName).Distinct().Count());
    }
}
