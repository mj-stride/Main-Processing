namespace PrivateTransportCleaning.Models
{
    public class ProcessingJob
    {
        public string JobId { get; set; }
        public string Status { get; set; } = "Queued"; // Queued, Processing, Done, Failed
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}