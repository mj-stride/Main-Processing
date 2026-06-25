using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Processing_Integrator.Pages
{
    public class IndexModel : PageModel
    {
        private readonly ServiceOptions _services;

        public string PubClean { get; private set; } = string.Empty;
        public string PrivClean { get; private set; } = string.Empty;
        public string MainProc { get; private set; } = string.Empty;
        public string ReportGen { get; private set; } = string.Empty;

        public IndexModel(IOptions<ServiceOptions> options)
        {
            _services = options.Value;
        }

        public void OnGet()
        {
            PubClean = _services.PubClean;
            PrivClean = _services.PrivClean;
            MainProc = _services.MainProc;
            ReportGen = _services.ReportGen;
        }
    }


}