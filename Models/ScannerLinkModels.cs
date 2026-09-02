namespace ImajinationAPI.Models
{
    public class CreateScannerLinkRequest
    {
        public string? label { get; set; }
        public DateTime? expiresAt { get; set; }
    }

    public class ResolveScannerLinkRequest
    {
        public string? token { get; set; }
    }
}
