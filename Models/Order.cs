namespace OrderVerificationAPI.Models
{
    public class Order
    {
        public int OrderId { get; set; }
        public string OrderNumber { get; set; }
        public int BranchId { get; set; }  // Foreign key for Branch
        public Branch Branch { get; set; } // Navigation property
        public string Recipient { get; set; }
        public string Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public decimal Amount { get; set; } // New property to store the extracted amount
    }
}
