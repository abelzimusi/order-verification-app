namespace OrderVerificationAPI.Interfaces
{
    public interface IOrderService
    {
        //Task<string> VerifyAndProcessOrder(string orderNumber, string sender);
        Task<string> VerifyAndProcessOrder(string orderNumber, string sender, string messageBody);
    }

}
