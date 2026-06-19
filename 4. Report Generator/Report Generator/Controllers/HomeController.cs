using Microsoft.AspNetCore.Mvc;

namespace Report_Generator.Controllers
{
    public class HomeController : Controller
    {
        // GET /Home/ReportGenerator  (also GET / via the default route in Program.cs)
        [HttpGet]
        public IActionResult ReportGenerator()
        {
            return View();
        }
    }
}
