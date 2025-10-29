namespace pdf_extractor.Models
{
    public sealed class OrderInfo
    {
        public string? OrderNumber { get; set; }
        public string? OrderDate { get; set; }
        public string? OrderClass { get; set; }
        public string? DeliveryWay { get; set; }
        public string? PaymentMethod { get; set; }
        public string? ItemsAmount { get; set; }
        public string? NetWeight { get; set; }

        // Optional: Helpful ToString override for debugging/logging
        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();

            if (!string.IsNullOrWhiteSpace(OrderNumber))
                sb.AppendLine($"[OrderInfo] Order Number: {OrderNumber}");

            if (!string.IsNullOrWhiteSpace(OrderDate))
                sb.AppendLine($"[OrderInfo] Order Date: {OrderDate}");

            if (!string.IsNullOrWhiteSpace(OrderClass))
                sb.AppendLine($"[OrderInfo] Order Class: {OrderClass}");

            if (!string.IsNullOrWhiteSpace(DeliveryWay))
                sb.AppendLine($"[OrderInfo] Delivery Way: {DeliveryWay}");

            if (!string.IsNullOrWhiteSpace(PaymentMethod))
                sb.AppendLine($"[OrderInfo] Payment Method: {PaymentMethod}");

            if (!string.IsNullOrWhiteSpace(ItemsAmount))
                sb.AppendLine($"[OrderInfo] Items Amount: {ItemsAmount}");

            if (!string.IsNullOrWhiteSpace(NetWeight))
                sb.AppendLine($"[OrderInfo] Net Weight: {NetWeight}");

            return sb.ToString().TrimEnd();
        }
    }
}