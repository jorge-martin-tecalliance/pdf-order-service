namespace pdf_extractor.Models
{
    public sealed class LineItem
    {
        public string? PartNumber { get; set; }
        public string? Description { get; set; }
        public int? Quantity { get; set; }
        public string? DiscountCode { get; set; }
        public decimal? DiscountPct { get; set; }
        public decimal? RetailPerUnit { get; set; }
        public decimal? NetPerUnit { get; set; }
        public decimal? NetSummary { get; set; }

        // Optional: Helpful ToString override for debugging/logging
        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();

            if (!string.IsNullOrWhiteSpace(PartNumber))
                sb.AppendLine($"[LineItem] Part Number: {PartNumber}");

            if (!string.IsNullOrWhiteSpace(Description))
                sb.AppendLine($"[LineItem] Description: {Description}");

            if (Quantity != 0)   // ✅ works for int
                sb.AppendLine($"[LineItem] Quantity: {Quantity}");

            if (!string.IsNullOrWhiteSpace(DiscountCode))
                sb.AppendLine($"[LineItem] Discount Code: {DiscountCode}");

            if (DiscountPct != 0)   // ✅ works for decimal
                sb.AppendLine($"[LineItem] Discount %: {DiscountPct}");

            if (RetailPerUnit != 0)
                sb.AppendLine($"[LineItem] Retail Per Unit: {RetailPerUnit}");

            if (NetPerUnit != 0)
                sb.AppendLine($"[LineItem] Net Per Unit: {NetPerUnit}");

            if (NetSummary != 0)
                sb.AppendLine($"[LineItem] Net Summary: {NetSummary}");

            return sb.ToString().TrimEnd();
        }
    }
}