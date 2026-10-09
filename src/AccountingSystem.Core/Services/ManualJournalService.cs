using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>
/// اسناد حسابداری دستی. ثبت سند جدید فقط با مدیر است؛ ابطال و ویرایش با مدیر یا کاربری که دسترسی لازم را در شعبه‌ی خودش دارد.
/// ویرایش با ابطال سند قبلی و ثبت نسخه‌ی جدید انجام می‌شود.
/// </summary>
public sealed class ManualJournalService
{
    public const string ReplacementReason = "ویرایش سند دستی؛ نسخه‌ی اصلاحی جایگزین شد";

    private readonly IAccountingRepository _repository;
    private readonly PermissionService _permissions;

    public ManualJournalService(IAccountingRepository repository)
    {
        _repository = repository;
        _permissions = new PermissionService(repository);
    }

    public async Task<long?> CreateAsync(CurrentUser actor, int branchId, string description, DateTime? occurredOn, IReadOnlyList<JournalLineDraft> lines, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        if (branchId <= 0)
        {
            throw new BusinessRuleException("شعبه‌ی سند را انتخاب کنید.");
        }
        var ledger = await _repository.GetBranchLedgerAsync(branchId, ct);
        var accounts = await LoadAccountsAsync(ct);
        var occurredAt = OccurrenceRules.Resolve(occurredOn, now);
        var posting = LedgerPlanner.PlanManual(ledger, accounts, description, lines, actor.Id, occurredAt, now);
        return await _repository.PostAsync(posting, ct);
    }

    public async Task VoidAsync(CurrentUser actor, long entryId, string reason, DateTime now, CancellationToken ct = default)
    {
        var entry = await LoadManualAsync(entryId, ct);
        await _permissions.RequireAsync(actor, Permission.ManualVoid, entry.BranchId, ct);
        var ledger = await _repository.GetBranchLedgerAsync(entry.BranchId, ct);
        var posting = LedgerPlanner.PlanVoid(ledger, new DocRef(LedgerDocKind.Manual, entryId), reason, actor.Id, now);
        await _repository.PostAsync(posting, ct);
    }

    public async Task<long?> EditAsync(CurrentUser actor, long entryId, string description, DateTime? occurredOn, IReadOnlyList<JournalLineDraft> lines, DateTime now, CancellationToken ct = default)
    {
        var entry = await LoadManualAsync(entryId, ct);
        await _permissions.RequireAsync(actor, Permission.ManualEdit, entry.BranchId, ct);
        var sameDay = occurredOn is null || occurredOn.Value.Date == entry.OccurredAt.Date;
        var occurredAt = sameDay ? entry.OccurredAt : OccurrenceRules.Resolve(occurredOn, now);
        var ledger = await _repository.GetBranchLedgerAsync(entry.BranchId, ct);
        var accounts = await LoadAccountsAsync(ct);
        var posting = LedgerPlanner.PlanManual(ledger, accounts, description, lines, actor.Id, occurredAt, now,
            new DocRef(LedgerDocKind.Manual, entryId), ReplacementReason);
        return await _repository.PostAsync(posting, ct);
    }

    public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) =>
        _repository.GetAccountsAsync(ct);

    /// <summary>یک سند حسابداری با سطرهایش؛ کاربر صندوق فقط سندهای شعبه‌ی خودش را می‌بیند.</summary>
    public async Task<JournalEntryInfo?> GetAsync(CurrentUser actor, long entryId, CancellationToken ct = default)
    {
        var entry = await _repository.GetJournalEntryAsync(entryId, ct);
        if (entry is not null)
        {
            BranchScope.ResolveForReport(actor, entry.BranchId);
        }
        return entry;
    }

    private async Task<JournalEntryInfo> LoadManualAsync(long entryId, CancellationToken ct)
    {
        var entry = await _repository.GetJournalEntryAsync(entryId, ct)
            ?? throw new BusinessRuleException("سند انتخابی یافت نشد.");
        if (entry.SourceType != SourceTypes.Manual)
        {
            throw new BusinessRuleException("فقط اسناد دستی را می‌توان مستقیماً ویرایش یا باطل کرد. سند معامله و افتتاحیه از بخش خودشان اصلاح می‌شود.");
        }
        if (entry.IsVoided)
        {
            throw new BusinessRuleException("این سند قبلاً باطل شده است.");
        }
        return entry;
    }

    private async Task<IReadOnlyDictionary<string, AccountInfo>> LoadAccountsAsync(CancellationToken ct)
    {
        var accounts = await _repository.GetAccountsAsync(ct);
        return accounts.ToDictionary(a => a.Code, StringComparer.Ordinal);
    }
}
