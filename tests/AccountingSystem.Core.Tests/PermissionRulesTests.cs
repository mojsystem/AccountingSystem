using System.Linq;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Xunit;

namespace AccountingSystem.Core.Tests;

public class PermissionRulesTests
{
    private static readonly UserAccess Admin = new(1, UserRole.Admin, true, null, new Dictionary<int, BranchAccess>());

    private static BranchAccess Branch(int branchId, string roleName, params Permission[] permissions) =>
        new(branchId, "شعبه " + branchId, branchId * 10, roleName, new HashSet<Permission>(permissions));

    private static UserAccess Member(int? defaultBranchId, params BranchAccess[] branches) =>
        new(7, UserRole.Cashier, true, defaultBranchId, branches.ToDictionary(b => b.BranchId));

    [Fact]
    public void Admin_is_allowed_everything_in_every_branch_and_can_read_all_branches()
    {
        foreach (var permission in Enum.GetValues<Permission>())
        {
            Assert.True(PermissionRules.IsAllowed(Admin, permission, 3));
            Assert.True(PermissionRules.IsAllowedSomewhere(Admin, permission));
        }
        Assert.True(PermissionRules.CanRead(Admin, 99));
        Assert.Null(PermissionRules.ResolveReadBranch(Admin, null));
        Assert.Equal<int?>(4, PermissionRules.ResolveReadBranch(Admin, 4));
    }

    [Fact]
    public void Member_without_the_task_is_denied_and_the_message_names_the_task()
    {
        var access = Member(1, Branch(1, "کاربر صندوق", Permission.TradeRecord));

        var ex = Assert.Throws<BusinessRuleException>(() => PermissionRules.Require(access, Permission.TradeEdit, 1));

        Assert.Contains("ویرایش معامله", ex.Message);
        PermissionRules.Require(access, Permission.TradeRecord, 1);
    }

    [Fact]
    public void Task_applies_only_in_the_branch_where_the_role_grants_it()
    {
        var access = Member(1, Branch(1, "حسابدار", Permission.TradeEdit));

        Assert.True(PermissionRules.IsAllowed(access, Permission.TradeEdit, 1));
        Assert.False(PermissionRules.IsAllowed(access, Permission.TradeEdit, 2));
        Assert.True(PermissionRules.IsAllowedSomewhere(access, Permission.TradeEdit));
        Assert.False(PermissionRules.IsAllowedSomewhere(access, Permission.RateSet));
    }

    [Fact]
    public void Non_member_branch_is_refused_for_writes_and_reads()
    {
        var access = Member(1, Branch(1, "مدیر شعبه", Enum.GetValues<Permission>()));

        var write = Assert.Throws<BusinessRuleException>(() => PermissionRules.Require(access, Permission.TradeRecord, 2));
        Assert.Contains("شعبه", write.Message);
        Assert.Throws<BusinessRuleException>(() => PermissionRules.RequireRead(access, 2));
        Assert.False(PermissionRules.CanRead(access, 2));
    }

    [Fact]
    public void Same_user_can_have_different_roles_in_different_branches()
    {
        var access = Member(
            1,
            Branch(1, "مدیر شعبه", Enum.GetValues<Permission>()),
            Branch(2, "کاربر صندوق", Permission.TradeRecord));

        Assert.True(PermissionRules.IsAllowed(access, Permission.OpeningCreate, 1));
        Assert.False(PermissionRules.IsAllowed(access, Permission.OpeningCreate, 2));
        Assert.True(PermissionRules.IsAllowed(access, Permission.TradeRecord, 2));
        Assert.True(PermissionRules.CanRead(access, 2));
    }

    [Fact]
    public void Membership_without_any_task_is_read_only()
    {
        var access = Member(1, Branch(1, "ناظر"));

        Assert.True(PermissionRules.CanRead(access, 1));
        Assert.False(PermissionRules.IsAllowed(access, Permission.TradeRecord, 1));
        Assert.False(PermissionRules.IsAllowedSomewhere(access, Permission.TradeRecord));
        PermissionRules.RequireRead(access, 1);
    }

    [Fact]
    public void Inactive_users_are_denied_even_when_admin()
    {
        var inactiveAdmin = Admin with { IsActive = false };
        var inactiveMember = Member(1, Branch(1, "مدیر شعبه", Enum.GetValues<Permission>())) with { IsActive = false };

        Assert.False(PermissionRules.CanRead(inactiveAdmin, 1));
        Assert.False(PermissionRules.IsAllowed(inactiveAdmin, Permission.RateSet, 1));
        Assert.False(PermissionRules.IsAllowed(inactiveMember, Permission.RateSet, 1));
        Assert.Throws<BusinessRuleException>(() => PermissionRules.Require(inactiveMember, Permission.RateSet, 1));
        Assert.Throws<BusinessRuleException>(() => PermissionRules.ResolveReadBranch(inactiveMember, null));
    }

    [Fact]
    public void Read_branch_defaults_to_the_default_membership_and_refuses_foreign_branches()
    {
        var access = Member(2, Branch(1, "حسابدار"), Branch(2, "کاربر صندوق", Permission.TradeRecord));

        Assert.Equal<int?>(2, PermissionRules.ResolveReadBranch(access, null));
        Assert.Equal<int?>(1, PermissionRules.ResolveReadBranch(access, 1));
        Assert.Throws<BusinessRuleException>(() => PermissionRules.ResolveReadBranch(access, 5));
    }

    [Fact]
    public void Read_branch_falls_back_to_the_lowest_membership_when_default_is_missing()
    {
        var access = Member(null, Branch(3, "ناظر"), Branch(2, "ناظر"));

        Assert.Equal<int?>(2, PermissionRules.ResolveReadBranch(access, null));
    }

    [Fact]
    public void User_without_any_membership_cannot_pick_a_read_branch()
    {
        var access = Member(null);

        Assert.Throws<BusinessRuleException>(() => PermissionRules.ResolveReadBranch(access, null));
    }

    [Fact]
    public void Admin_role_cannot_be_assigned_branch_roles()
    {
        Assert.Throws<BusinessRuleException>(() => PermissionRules.EnsureAssignable(Admin));
        PermissionRules.EnsureAssignable(Member(null));
    }

    [Fact]
    public void Role_names_are_trimmed_and_length_checked()
    {
        Assert.Equal("حسابدار", PermissionRules.ValidateRoleName("  حسابدار  "));
        Assert.Throws<BusinessRuleException>(() => PermissionRules.ValidateRoleName(" "));
        Assert.Throws<BusinessRuleException>(() => PermissionRules.ValidateRoleName("ا"));
        Assert.Throws<BusinessRuleException>(() => PermissionRules.ValidateRoleName(new string('ا', 61)));
        Assert.Equal(new string('ا', 60), PermissionRules.ValidateRoleName(new string('ا', 60)));
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
        Assert.False(PermissionCodes.TryParse(null, out _));
    }

    [Fact]
    public void Every_permission_has_a_distinct_code_and_display_name()
    {
        var all = Enum.GetValues<Permission>();

        Assert.Equal(10, all.Length);
        Assert.Equal(all.Length, all.Select(PermissionCodes.ToCode).Distinct().Count());
        Assert.Equal(all.Length, all.Select(PermissionCodes.DisplayName).Distinct().Count());
    }

    [Fact]
    public void Default_role_presets_match_the_agreed_permission_sets()
    {
        var presets = RolePresets.All.ToDictionary(p => p.Name);

        Assert.Equal(3, presets.Count);
        Assert.Equal(10, presets[RolePresets.BranchManager].Permissions.Distinct().Count());
        Assert.Equal(
            new[] { Permission.TradeRecord },
            presets[RolePresets.Cashier].Permissions.ToArray());

        var accountant = presets[RolePresets.Accountant].Permissions.ToHashSet();
        Assert.Equal(7, accountant.Count);
        Assert.DoesNotContain(Permission.TradeRecord, accountant);
        Assert.DoesNotContain(Permission.OpeningCreate, accountant);
        Assert.DoesNotContain(Permission.RateSet, accountant);
        Assert.Contains(Permission.ManualVoid, accountant);
    }
}
