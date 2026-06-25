using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Report_Generator.Controllers
{
    public class HomeController : Controller
    {
        private readonly ServiceOptions _services;

        public HomeController(IOptions<ServiceOptions> options)
        {
            _services = options.Value;
        }

        // GET /Home/ReportGenerator  (also GET / via the default route in Program.cs)
        [HttpGet]
        public IActionResult ReportGenerator()
        {
            return View();
        }

        public IActionResult GoToDashboard()
        {
            return Redirect(_services.Dashboard);
        }
    }
}