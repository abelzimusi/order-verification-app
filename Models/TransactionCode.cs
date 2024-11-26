namespace OrderVerificationAPI.Models
{
    public class TransactionCode
    {
        public int Id { get; set; }
        public string Code { get; set; } // The transaction code extracted from the image
        public string PhoneNumber { get; set; } // The sender's phone number
        public DateTime Timestamp { get; set; } // Time when this record was saved
    }
}
