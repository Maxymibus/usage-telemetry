
using Microsoft.AspNetCore.Mvc;

namespace AiUseCaseDiscovery.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();
}
