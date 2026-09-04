using LongJourney.Core;
using Microsoft.AspNetCore.Mvc;

namespace LongJourney.Server.Pages.Inspect;

public sealed class TraceModel(IInspectionReader reader) : InspectionPageModel
{
    public InspectionTrace? Data { get; private set; }
    public HashSet<string> VisibleIds { get; } = new(StringComparer.Ordinal);
    public IActionResult OnGet(string id)
    {
        Data = reader.ReadTrace(id);
        if (Data is null)
        {
            return Missing("기억");
        }

        foreach (var memory in Data.Memories)
        {
            VisibleIds.Add(memory.Id);
        }

        return Page();
    }
}
