using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>
/// مدیریت سرفصل حساب‌ها. فقط مدیر سیستم حساب اضافه، ویرایش یا حذف می‌کند.
/// قواعد ساختار (سطح، کد، نوع، حساب‌های پایه) در <see cref="AccountRules"/> است.
/// </summary>
public sealed class AccountService
{
    private readonly IAccountingRepository _repository;

    public AccountService(IAccountingRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CurrentUser actor, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        return await _repository.GetAccountsAsync(ct);
    }

    /// <summary>حساب تازه می‌سازد. اگر parentCode خالی باشد، گروه تازه (سطح ۱) ساخته می‌شود.</summary>
    public async Task CreateAsync(CurrentUser actor, string? code, string? name, string? parentCode, string? accountType, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        var cleanCode = AccountRules.CleanCode(code);
        var cleanName = AccountRules.CleanName(name);
        var accounts = await _repository.GetAccountsAsync(ct);
        var parent = FindParent(accounts, parentCode);
        var type = ResolveType(parent, accountType);
        if (accounts.Any(a => a.Code == cleanCode))
        {
            throw new BusinessRuleException($"حسابی با کد «{cleanCode}» از قبل وجود دارد.");
        }
        var level = AccountRules.ValidateNew(cleanCode, parent, type);
        await _repository.AddAccountAsync(new AccountRecord(cleanCode, cleanName, level, parent?.Code, type, true), actor.Id, now, ct);
    }

    /// <summary>حساب موجود را ویرایش می‌کند. کد و نام و نوع و پدر و وضعیت را طبق قواعد <see cref="AccountRules"/> می‌پذیرد.</summary>
    public async Task UpdateAsync(CurrentUser actor, string originalCode, string? code, string? name, string? parentCode, string? accountType, bool isActive, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        var accounts = await _repository.GetAccountsAsync(ct);
        var current = accounts.FirstOrDefault(a => a.Code == originalCode)
            ?? throw new BusinessRuleException("حساب مورد نظر پیدا نشد.");
        var newCode = AccountRules.CleanCode(code);
        var newName = AccountRules.CleanName(name);
        var newParent = FindParent(accounts, parentCode);
        var newType = ResolveType(newParent, accountType);
        if (newCode != current.Code && accounts.Any(a => a.Code == newCode))
        {
            throw new BusinessRuleException($"حسابی با کد «{newCode}» از قبل وجود دارد.");
        }
        var level = AccountRules.ValidateChange(current, newCode, newType, newParent?.Code, newParent, isActive);
        await _repository.UpdateAccountAsync(current.Code,
            new AccountRecord(newCode, newName, level, newParent?.Code, newType, isActive), actor.Id, now, ct);
    }

    public async Task DeleteAsync(CurrentUser actor, string code, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        var accounts = await _repository.GetAccountsAsync(ct);
        var account = accounts.FirstOrDefault(a => a.Code == code)
            ?? throw new BusinessRuleException("حساب مورد نظر پیدا نشد.");
        AccountRules.ValidateDelete(account);
        await _repository.DeleteAccountAsync(account.Code, actor.Id, now, ct);
    }

    private static AccountInfo? FindParent(IReadOnlyList<AccountInfo> accounts, string? parentCode)
    {
        var code = parentCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }
        return accounts.FirstOrDefault(a => a.Code == code)
            ?? throw new BusinessRuleException("حساب پدر پیدا نشد.");
    }

    /// <summary>زیرمجموعه نوع را از پدر می‌گیرد؛ گروه باید نوع خودش را داشته باشد.</summary>
    private static string ResolveType(AccountInfo? parent, string? accountType)
    {
        if (parent is null || !string.IsNullOrWhiteSpace(accountType))
        {
            return AccountRules.CleanType(accountType);
        }
        return parent.AccountType;
    }
}
