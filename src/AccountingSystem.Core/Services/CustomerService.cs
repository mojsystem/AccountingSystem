using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>
/// مشتریان صرافی. فهرست مشترک همه‌ی شعبه‌هاست و هر کاربر فعالی که به حداقل یک شعبه دسترسی دارد آن را می‌بیند.
/// ثبت و ویرایش مشتری برای هر کسی است که وظیفه‌ی «ثبت معامله» را در هر شعبه‌ای دارد (و مدیر سیستم).
/// </summary>
public sealed class CustomerService
{
    private readonly IAccountingRepository _repository;
    private readonly PermissionService _permissions;

    public CustomerService(IAccountingRepository repository, PermissionService permissions)
    {
        _repository = repository;
        _permissions = permissions;
    }

    /// <summary>فهرست مشتریان؛ take سقف تعداد ردیف است (فرم‌های معامله فهرست بزرگ‌تری می‌خواهند).</summary>
    public async Task<IReadOnlyList<CustomerInfo>> SearchAsync(CurrentUser user, string? search, CancellationToken ct = default, int take = 500)
    {
        await RequireMemberAsync(user, ct);
        return await _repository.GetCustomersAsync(CustomerRules.NormalizeSearch(search), Math.Clamp(take, 1, 5000), ct);
    }

    public async Task<CustomerInfo> GetAsync(CurrentUser user, int id, CancellationToken ct = default)
    {
        await RequireMemberAsync(user, ct);
        return await _repository.GetCustomerAsync(id, ct) ?? throw new BusinessRuleException("مشتری مورد نظر پیدا نشد.");
    }

    /// <summary>آیا کاربر می‌تواند مشتری ثبت یا ویرایش کند؟ (مدیر، یا دارنده‌ی «ثبت معامله» در حداقل یک شعبه)</summary>
    public async Task<bool> CanEditAsync(CurrentUser user, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin)
        {
            return true;
        }
        return await _permissions.HasAnyAsync(user, Permission.TradeRecord, ct);
    }

    public async Task<int> CreateAsync(CurrentUser user, CustomerInput input, DateTime now, CancellationToken ct = default)
    {
        await RequireEditAsync(user, ct);
        return await _repository.AddCustomerAsync(CustomerRules.Clean(input), user.Id, now, ct);
    }

    public async Task UpdateAsync(CurrentUser user, int id, CustomerInput input, DateTime now, CancellationToken ct = default)
    {
        await RequireEditAsync(user, ct);
        await _repository.UpdateCustomerAsync(id, CustomerRules.Clean(input), user.Id, now, ct);
    }

    private async Task RequireMemberAsync(CurrentUser user, CancellationToken ct)
    {
        var access = await _permissions.GetAccessAsync(user, ct);
        if (!access.IsActive || (access.Role != UserRole.Admin && access.Branches.Count == 0))
        {
            throw new BusinessRuleException("برای دیدن مشتریان باید به حداقل یک شعبه‌ی فعال دسترسی داشته باشید.");
        }
    }

    private async Task RequireEditAsync(CurrentUser user, CancellationToken ct)
    {
        if (!await CanEditAsync(user, ct))
        {
            throw new BusinessRuleException("برای ثبت و ویرایش مشتری باید وظیفه‌ی «ثبت معامله» را در حداقل یک شعبه داشته باشید.");
        }
    }
}
