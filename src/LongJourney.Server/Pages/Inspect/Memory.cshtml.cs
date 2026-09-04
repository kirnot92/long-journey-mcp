using LongJourney.Core;
using Microsoft.AspNetCore.Mvc;

namespace LongJourney.Server.Pages.Inspect;

public sealed class MemoryModel(IInspectionReader reader) : InspectionPageModel
{
    public MemoryRecord? Data { get; private set; }
    public IActionResult OnGet(string id)
    {
        Data = reader.GetMemory(id);
        return Data is null ? Missing("기억") : Page();
    }
}
