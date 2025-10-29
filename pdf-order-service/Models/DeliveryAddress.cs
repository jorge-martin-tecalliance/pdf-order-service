namespace pdf_extractor.Models
{
    public sealed class DeliveryAddress
    {
        public string? RecipientName { get; set; }
        public string? CompanyName { get; set; }
        public string? Street { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? ZipCode { get; set; }
        public string? Country { get; set; }


        // Optional: Helpful ToString override for debugging/logging
        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();

            if (!string.IsNullOrWhiteSpace(RecipientName))
                sb.AppendLine($"[Delivery] Recipient: {RecipientName}");

            if (!string.IsNullOrWhiteSpace(CompanyName))
                sb.AppendLine($"[Delivery] Company: {CompanyName}");

            if (!string.IsNullOrWhiteSpace(Street))
                sb.AppendLine($"[Delivery] Street: {Street}");

            if (!string.IsNullOrWhiteSpace(City))
                sb.AppendLine($"[Delivery] City: {City}");

            if (!string.IsNullOrWhiteSpace(State))
                sb.AppendLine($"[Delivery] State: {State}");

            if (!string.IsNullOrWhiteSpace(ZipCode))
                sb.AppendLine($"[Delivery] ZipCode: {ZipCode}");

            if (!string.IsNullOrWhiteSpace(Country))
                sb.AppendLine($"[Delivery] Country: {Country}");

            return sb.ToString().TrimEnd();
        }
    }
}