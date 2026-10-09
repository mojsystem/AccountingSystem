using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Security;

namespace AccountingSystem.Core.Services;

/// <summary>ورود، ایجاد کاربر و مدیریت کاربران. کاربر صندوق باید به یک شعبه تعلق داشته باشد.</summary>
public sealed class UserService
{
    private const int MinPasswordLength = 6;
    private readonly IAccountingRepository _repository;

    public UserService(IAccountingRepository repository)
    {
        _repository = repository;
    }

    public Task<int> CountUsersAsync(CancellationToken ct = default) => _repository.CountUsersAsync(ct);

    /// <summary>در صورت صحیح بودن نام کاربری و رمز، کاربر جاری را برمی‌گرداند؛ وگرنه null.</summary>
    public async Task<CurrentUser?> SignInAsync(string username, string password, CancellationToken ct = default)
    {
        var account = await _repository.GetUserByUsernameAsync((username ?? string.Empty).Trim(), ct);
        if (account is null || !account.IsActive)
        {
            return null;
        }
        return PasswordHashing.Verify(password ?? string.Empty, account.PasswordHash)
            ? new CurrentUser(account.Id, account.Username, account.FullName, account.Role, account.BranchId, account.BranchName)
            : null;
    }

    /// <summary>فقط وقتی هیچ کاربری وجود ندارد، نخستین مدیر سیستم ساخته می‌شود.</summary>
    public async Task<CurrentUser> CreateFirstAdminAsync(string username, string fullName, string password, DateTime now, CancellationToken ct = default)
    {
        if (await _repository.CountUsersAsync(ct) > 0)
        {
            throw new BusinessRuleException("کاربری قبلاً ثبت شده است.");
        }
        var id = await CreateUserCoreAsync(username, fullName, UserRole.Admin, null, password, now, ct);
        return new CurrentUser(id, (username ?? string.Empty).Trim(), (fullName ?? string.Empty).Trim(), UserRole.Admin, null, null);
    }

    /// <summary>
    /// ایجاد کاربر (فقط مدیر). برای کاربر صندوق شعبه الزامی است؛ مدیر به همه‌ی شعبه‌ها دسترسی دارد و شعبه برایش ذخیره نمی‌شود.
    /// </summary>
    public async Task CreateUserAsync(CurrentUser actor, string username, string fullName, string password, UserRole role, int? branchId, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        int? storedBranch = null;
        if (role == UserRole.Cashier)
        {
            if (branchId is not { } selected || selected <= 0)
            {
                throw new BusinessRuleException("برای کاربر صندوق، شعبه را انتخاب کنید.");
            }
            var branches = await _repository.GetBranchesAsync(ct);
            if (branches.All(b => b.Id != selected))
            {
                throw new BusinessRuleException("شعبه‌ی انتخابی یافت نشد.");
            }
            storedBranch = selected;
        }
        await CreateUserCoreAsync(username, fullName, role, storedBranch, password, now, ct);
    }

    public async Task<IReadOnlyList<UserInfo>> GetUsersAsync(CurrentUser actor, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        return await _repository.GetUsersAsync(ct);
    }

    private async Task<int> CreateUserCoreAsync(string username, string fullName, UserRole role, int? branchId, string password, DateTime now, CancellationToken ct)
    {
        var cleanUser = (username ?? string.Empty).Trim();
        var cleanName = (fullName ?? string.Empty).Trim();
        var cleanPassword = password ?? string.Empty;
        if (cleanUser.Length < 3 || cleanUser.Length > 50)
        {
            throw new BusinessRuleException("نام کاربری باید بین ۳ تا ۵۰ کاراکتر باشد.");
        }
        if (cleanName.Length == 0)
        {
            throw new BusinessRuleException("نام و نام خانوادگی را وارد کنید.");
        }
        if (cleanPassword.Length < MinPasswordLength)
        {
            throw new BusinessRuleException($"رمز عبور باید حداقل {MinPasswordLength} کاراکتر باشد.");
        }
        if (await _repository.GetUserByUsernameAsync(cleanUser, ct) is not null)
        {
            throw new BusinessRuleException("این نام کاربری قبلاً ثبت شده است.");
        }
        return await _repository.AddUserAsync(cleanUser, cleanName, role, branchId, PasswordHashing.Hash(cleanPassword), now, ct);
    }
}
