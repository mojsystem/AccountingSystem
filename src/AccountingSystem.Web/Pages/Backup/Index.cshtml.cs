using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Backup;

/// <summary>پشتیبان و بازیابی پایگاه داده. فقط مدیر سیستم (AdminOnly).</summary>
public class IndexModel : PageModel
{
    private readonly BackupService _backups;

    public IndexModel(BackupService backups)
    {
        _backups = backups;
    }

    public DatabaseStatusInfo? Status { get; private set; }

    public IReadOnlyList<BackupInfo> Backups { get; private set; } = Array.Empty<BackupInfo>();

    [BindProperty]
    public string? RestorePath { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        try
        {
            var backup = await _backups.CreateBackupAsync(User.ToCurrentUser(), DateTime.Now, ct);
            TempData["Success"] = "پشتیبان ساخته شد: " + backup.FilePath;
        }
        catch (Exception ex) when (ex is BusinessRuleException or SchemaUpgradeException)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRestoreAsync(CancellationToken ct)
    {
        try
        {
            await _backups.RestoreAsync(User.ToCurrentUser(), RestorePath, DateTime.Now, ct);
            TempData["Success"] = "بازیابی انجام شد و نسخه‌ی پایگاه داده به‌روز شد. برای اطمینان صفحه‌ها را دوباره باز کنید.";
        }
        catch (Exception ex) when (ex is BusinessRuleException or SchemaUpgradeException)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var user = User.ToCurrentUser();
        Status = await _backups.GetStatusAsync(user, ct);
        Backups = await _backups.ListBackupsAsync(user, ct);
    }
}
