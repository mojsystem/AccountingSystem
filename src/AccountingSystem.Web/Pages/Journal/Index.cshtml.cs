using System.Globalization;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AccountingSystem.Web.Pages.Journal;

public class IndexModel : PageModel
{
    private readonly ReportService _reports;

    public IndexModel(ReportService reports)
    {
        _reports = reports;
    }

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? To { get; set; }

    public IReadOnlyList<JournalEntryInfo> Entries { get; private set; } = Array.Empty<JournalEntryInfo>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        var today = DateTime.Now.Date;
        var from = ParseDate(From) ?? today;
        var to = ParseDate(To) ?? today;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        From = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        To = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Entries = await _reports.GetJournalAsync(from, to.AddDays(1), ct);
    }

    private static DateTime? ParseDate(string? text)
    {
        return DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : (DateTime?)null;
    }
}
