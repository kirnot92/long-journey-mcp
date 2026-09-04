using LongJourney.Core;
using Microsoft.AspNetCore.Mvc;

namespace LongJourney.Server.Pages.Inspect;

public sealed class IndexModel(IInspectionReader reader) : InspectionPageModel
{
    public InspectionOverview? Data { get; private set; }
    public int? Depth { get; private set; }
    public string? Query { get; private set; }
    public long? Revision { get; private set; }

    public IActionResult OnGet(int p = 1, int? depth = null, string? q = null, long? snapshot = null, long? revision = null)
    {
        Depth = depth;
        Query = q;
        Revision = revision;
        if (!ModelState.IsValid || p < 1 || depth < 0 || snapshot < 0 || revision < 0 || q?.Length > 200)
        {
            return InvalidQuery();
        }

        Data = reader.BrowseMemories(new InspectionMemoryQuery(p, depth, q, snapshot, revision));
        return Page();
    }
}
