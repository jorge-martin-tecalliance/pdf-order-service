namespace pdf_extractor.Configuration
{
    public sealed class AppCredentialsOptions
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string SellerId { get; set; } = "";
        public string DefaultLocation { get; set; } = "";
        public string InboundPdfFolder { get; set; } = "";
        public string ArchivedPdfFolder { get; set; } = "";
    }
}
