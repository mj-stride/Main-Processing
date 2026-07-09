namespace Ttds.Shared;

public class ServiceOptions
{
    public const string SectionName = "Services";

    public string Dashboard { get; set; } = string.Empty;
    public string PubClean { get; set; } = string.Empty;
    public string PrivClean { get; set; } = string.Empty;
    public string MainProc { get; set; } = string.Empty;
    public string ReportGen { get; set; } = string.Empty;
}