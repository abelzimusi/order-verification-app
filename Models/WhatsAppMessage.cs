namespace OrderVerificationAPI.Models
{
    public class WhatsAppMessage
    {
        public string Text { get; set; }    // The message content
        public string Sender { get; set; }  // The sender's phone number
        public string MessageId { get; set; } // (Optional) Message identifier
    }

}
