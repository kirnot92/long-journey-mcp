using LongJourney.Core;
using Microsoft.AspNetCore.Mvc;

namespace LongJourney.Server.Pages.Inspect;

public sealed class RunsModel(IInspectionReader reader) : InspectionPageModel
{
    public InspectionPage<InspectionRun>? Data { get; private set; }
    public IActionResult OnGet(int p = 1, long? snapshot = null)
    {
        if (!ModelState.IsValid || p < 1 || snapshot < 0)
        {
            return InvalidQuery();
        }

        Data = reader.BrowseRuns(p, snapshot);
        return Page();
    }
}
