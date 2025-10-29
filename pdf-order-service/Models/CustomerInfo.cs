namespace pdf_extractor.Models
{
    public sealed class CustomerInfo
    {
        public string? DmsNumber { get; set; }
        public string? Customer { get; set; }
        public string? Company { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? CtdiId { get; set; }

        // Optional: Helpful ToString override for debugging/logging
        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();

            if (!string.IsNullOrWhiteSpace(DmsNumber))
                sb.AppendLine($"[CustomerInfo] DMS Customer Number: {DmsNumber}");

            if (!string.IsNullOrWhiteSpace(Customer))
                sb.AppendLine($"[CustomerInfo] Customer: {Customer}");

            if (!string.IsNullOrWhiteSpace(Company))
                sb.AppendLine($"[CustomerInfo] Company: {Company}");

            if (!string.IsNullOrWhiteSpace(Email))
                sb.AppendLine($"[CustomerInfo] Email: {Email}");

            if (!string.IsNullOrWhiteSpace(Phone))
                sb.AppendLine($"[CustomerInfo] Phone: {Phone}");

            if (!string.IsNullOrWhiteSpace(CtdiId))
                sb.AppendLine($"[CustomerInfo] CTDI ID: {CtdiId}");

            return sb.ToString().TrimEnd();
        }
    }
}