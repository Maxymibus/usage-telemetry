
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AiUseCaseDiscovery.Data;
using AiUseCaseDiscovery.Models;

namespace AiUseCaseDiscovery.Controllers;

[Authorize]
public class SettingsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<IdentityUser> _userManager;

    public SettingsController(ApplicationDbContext db, UserManager<IdentityUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        var settings = await _db.UserAiSettings.FirstOrDefaultAsync(x => x.UserId == user!.Id)
                       ?? new UserAiSettings();
        return View(settings);
    }

    [HttpPost]
    public async Task<IActionResult> Save(UserAiSettings model)
    {
        var user = await _userManager.GetUserAsync(User);
        model.UserId = user!.Id;

        var existing = await _db.UserAiSettings.FirstOrDefaultAsync(x => x.UserId == user.Id);
        if (existing == null)
            _db.UserAiSettings.Add(model);
        else
        {
            existing.AzureOpenAiEndpoint = model.AzureOpenAiEndpoint;
            existing.AzureOpenAiApiKey = model.AzureOpenAiApiKey;
            existing.DeploymentName = model.DeploymentName;
        }
        await _db.SaveChangesAsync();
        return RedirectToAction("Index");
    }
}
