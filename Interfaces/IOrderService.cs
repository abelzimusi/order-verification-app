using OrderVerificationAPI.Models;

namespace OrderVerificationAPI.Interfaces
{
    public interface IOrderService
    {
        //Task<string> VerifyAndProcessOrder(string orderNumber, string sender);
        Task<string> VerifyAndProcessOrder(string orderNumber, string sender, string messageBody);
        Task SendMessage(string recipient, string text);
        Task<string> ProcessAndRespondToMessage(string sender, string message);
        Task<string> HandleKeywordBasedResponses(string sender, string message);
        DateTime ConvertUnixTimestampToDateTime(long unixTimestamp);
        public bool IsMessageRecent(DateTime messageTime, int thresholdMinutes = 10);
        //Task<string> ProcessOutgoingMessage(string text);
        //Task<List<Branch>> GetBranchesFromDatabase();
    }

}
