using System.Globalization;
using LongJourney.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LongJourney.Server.Pages;

public abstract class InspectionPageModel : PageModel
{
    public string? Error { get; private set; }

    protected IActionResult InvalidQuery()
    {
        Error = "필터 값이 올바르지 않습니다. 페이지는 1 이상, depth와 조회 상한은 0 이상의 정수이며 검색어는 200자 이하여야 합니다.";
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return Page();
    }

    protected IActionResult Missing(string item)
    {
        Error = item + "을(를) 찾을 수 없습니다. ID를 확인해 주세요.";
        Response.StatusCode = StatusCodes.Status404NotFound;
        return Page();
    }

    public static string At(DateTimeOffset? at)
    {
        return at?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "기록 없음";
    }

    public static string Usd(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture) + " USD";
    public static string Kind(RunKind kind) => kind == RunKind.Dream ? "Dream" : "Meditation";
}
