namespace pdf_extractor.Models
{
    public sealed class OrderDocument
    {
        public DeliveryAddress? DeliveryAddress { get; set; }
        public OrderInfo? OrderInfo { get; set; }
        public CustomerInfo? CustomerInfo { get; set; }
        public List<LineItem> Items { get; set; } = new();
    }
}