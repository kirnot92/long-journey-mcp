using LongJourney.Core;
using Microsoft.AspNetCore.Mvc;

namespace LongJourney.Server.Pages.Inspect;

public sealed class WorkModel(IInspectionReader reader) : InspectionPageModel
{
    public InspectionWorkDetail? Data { get; private set; }
    public IActionResult OnGet(long id, string? key)
    {
        if (!ModelState.IsValid || id < 1 || string.IsNullOrEmpty(key))
        {
            return InvalidQuery();
        }

        Data = reader.InspectWork(id, key);
        return Data is null ? Missing("작업") : Page();
    }
}
