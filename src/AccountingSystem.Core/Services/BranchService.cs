using System.Text.RegularExpressions;
using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>مدیریت شعبه‌ها (فقط مدیر). هر شعبه با ساخته شدن، صندوق‌ها و بهای تمام‌شده‌ی همه‌ی ارزها را می‌گیرد.</summary>
public sealed class BranchService
{
    private static readonly Regex CodePattern = new("^[A-Z0-9]{1,10}$", RegexOptions.CultureInvariant);
    private readonly IAccountingRepository _repository;

    public BranchService(IAccountingRepository repository)
    {
        _repository = repository;
    }

    public Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default) =>
        _repository.GetBranchesAsync(ct);

    public async Task<int> CreateBranchAsync(CurrentUser actor, string code, string name, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        var normalizedCode = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern.IsMatch(normalizedCode))
        {
            throw new BusinessRuleException("کد شعبه باید ۱ تا ۱۰ حرف یا رقم انگلیسی باشد (مثلاً MAIN یا B2).");
        }
        var cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0 || cleanName.Length > 100)
        {
            throw new BusinessRuleException("نام شعبه را وارد کنید (حداکثر ۱۰۰ کاراکتر).");
        }

        return await _repository.AddBranchAsync(normalizedCode, cleanName, now, ct);
    }
}
