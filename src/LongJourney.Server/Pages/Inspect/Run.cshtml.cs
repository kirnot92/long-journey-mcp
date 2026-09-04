using LongJourney.Core;
using Microsoft.AspNetCore.Mvc;

namespace LongJourney.Server.Pages.Inspect;

public sealed class RunModel(IInspectionReader reader) : InspectionPageModel
{
    public InspectionRunDetail? Data { get; private set; }
    public IActionResult OnGet(long id, int p = 1)
    {
        if (!ModelState.IsValid || p < 1 || id < 1)
        {
            return InvalidQuery();
        }

        Data = reader.InspectRun(id, p);
        return Data is null ? Missing("실행") : Page();
    }
}
