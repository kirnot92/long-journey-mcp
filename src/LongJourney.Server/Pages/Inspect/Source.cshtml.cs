using LongJourney.Core;
using Microsoft.AspNetCore.Mvc;

namespace LongJourney.Server.Pages.Inspect;

public sealed class SourceModel(IInspectionReader reader) : InspectionPageModel
{
    public InspectionSource? Data { get; private set; }
    public IActionResult OnGet(string id, int p = 1, long? snapshot = null)
    {
        if (!ModelState.IsValid || p < 1 || snapshot < 0)
        {
            return InvalidQuery();
        }

        Data = reader.InspectSource(id, p, snapshot);
        return Data is null ? Missing("Source") : Page();
    }
}
