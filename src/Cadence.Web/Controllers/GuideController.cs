using Cadence.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cadence.Web.Controllers;

public sealed class GuideController(SettingsService settings, AiClient ai) : Controller
{
    [HttpGet("/guide")]
    public async Task<IActionResult> Index()
    {
        ViewData["Settings"] = await settings.GetAsync();
        ViewData["Ai"] = await ai.StatusAsync();
        return View();
    }
}
