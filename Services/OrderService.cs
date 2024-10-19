using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using OrderVerificationAPI.Interfaces;
using OrderVerificationAPI.Models;

namespace OrderVerificationAPI.Services
{
    public class OrderService : IOrderService
    {
        private readonly AppDbContext _context;
        private readonly UltraMsgSettings _ultraMsgSettings;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly List<string> allowedSenders = new List<string>
        {
            "27677103182@c.us" // Add more allowed numbers here
        };
        public OrderService(AppDbContext context, IOptions<UltraMsgSettings> ultraMsgSettings, IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _ultraMsgSettings = ultraMsgSettings.Value;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<string> VerifyAndProcessOrder(string orderNumber, string sender, string jsonBody)
        {
            Console.WriteLine($"VerifyAndProcessOrder called with orderNumber: {orderNumber}, sender: {sender}");

            // Check if the sender is allowed
            if (!allowedSenders.Contains(sender))
            {
                return ""; // Unauthorized sender
            }

            // Parse the JSON to extract the "body" field
            var body = ExtractMessageBody(jsonBody);
            if (string.IsNullOrEmpty(body) || !body.StartsWith("ID-", StringComparison.OrdinalIgnoreCase))
            {
                return ""; // Invalid message format
            }

            // Check if the message contains a "ProcessedByService" tag to prevent reprocessing outgoing messages
            if (body.Contains("[MWZ~ChatBotService]"))
            {
                Console.WriteLine("Skipping message already processed by the service.");
                return ""; // Skip processing this message
            }

            // Determine if the order is a grocery order
            bool isGroceryOrder = IsGroceryOrder(body);

            // Extract order number, amount, branch, and recipient from the message body
            var extractedOrderNumber = ExtractOrderNumber(body);
            var extractedAmount = isGroceryOrder ? ExtractTotalAmount(body) : ExtractAmount(body);
            //var extractedAmount = ExtractAmount(body);
            var extractedBranchName = ExtractBranch(body);
            var recipient = ExtractRecipient(body);

            if (string.IsNullOrEmpty(extractedOrderNumber) || extractedAmount == null || string.IsNullOrEmpty(extractedBranchName))
            {
                Console.WriteLine("Failed to extract order number, amount, or branch from the message.");
                return ""; // Invalid message content
            }

            // Retrieve branches from the database
            var branches = await _context.Branches.ToListAsync();
            var matchedBranch = branches.FirstOrDefault(branch => body.Contains(branch.Name, StringComparison.OrdinalIgnoreCase));
            if (matchedBranch == null)
            {
                await SendMessage(sender, "Error: Branch not found in the message.");
                return "Branch not found.";
            }
            Console.WriteLine($"Matched Branch: {matchedBranch.Name}");

            // Find the maximum existing order number in the database
            var maxOrder = await _context.Orders.OrderByDescending(o => o.OrderNumber).FirstOrDefaultAsync();
            int newOrderNumber = DetermineNewOrderNumber(maxOrder, extractedOrderNumber);

            // If the extracted order number is smaller than the max order number in the database
            if (int.TryParse(extractedOrderNumber, out int extractedOrderInt) && extractedOrderInt <= int.Parse(maxOrder.OrderNumber))
            {
                newOrderNumber = int.Parse(maxOrder.OrderNumber) + 1;
                Console.WriteLine("Order number is outdated, correcting to: " + newOrderNumber);
            }

            // Construct a complete order message with a tag to avoid re-processing
            var completeMessage = $"{body} [MWZ~ChatBotService]";

            // Check if the order already exists
            var existingOrder = await _context.Orders.FirstOrDefaultAsync(o => o.OrderNumber == extractedOrderNumber);
            if (existingOrder != null)
            {
                // If the existing order is already corrected, skip reprocessing
                if (existingOrder.OrderNumber == newOrderNumber.ToString())
                {
                    Console.WriteLine("Order has already been corrected. Skipping further processing.");
                    return newOrderNumber.ToString();
                }

                // Duplicate order detected, use new order number
                Console.WriteLine("Duplicate order detected, generating a new order number.");

                // Create a new order with the corrected number
                await CreateNewOrder(newOrderNumber.ToString(), matchedBranch.BranchId, recipient, extractedAmount.Value, isGroceryOrder);

                // Notify the sender and the branch with the corrected order number
                await SendMessage(sender, $"Duplicate order detected. Corrected order number: {newOrderNumber}");

                // Send the corrected message to the recipient (branch phone number)
                await SendMessage(matchedBranch.PhoneNumber, completeMessage.Replace(extractedOrderNumber, newOrderNumber.ToString()));

                // Send the corrected message to the admin as well
                await SendMessage(matchedBranch.AdminPhoneNumber, completeMessage.Replace(extractedOrderNumber, newOrderNumber.ToString()));

                return newOrderNumber.ToString();
            }

            // If no duplicate order, create a new one
            await CreateNewOrder(extractedOrderNumber, matchedBranch.BranchId, recipient, extractedAmount.Value, isGroceryOrder);

            // Notify the branch and admin with the complete message
            await SendMessage(matchedBranch.PhoneNumber, completeMessage);
            await SendMessage(matchedBranch.AdminPhoneNumber, completeMessage);

            Console.WriteLine("Order processed successfully.");
            return "Order processed successfully.";
        }

        private async Task CreateNewOrder(string orderNumber, int branchId, string recipient, decimal amount)
        {
            var newOrder = new Order
            {
                OrderNumber = orderNumber,
                BranchId = branchId,
                Recipient = recipient,
                Status = "New",
                CreatedAt = DateTime.Now,
                Amount = amount
            };
            _context.Orders.Add(newOrder);
            await _context.SaveChangesAsync();
        }


        // Create a new order with the IsGrocery field
        private async Task CreateNewOrder(string orderNumber, int branchId, string recipient, decimal amount, bool isGroceryOrder)
        {
            var newOrder = new Order
            {
                OrderNumber = orderNumber,
                BranchId = branchId,
                Recipient = recipient,
                Status = "New",
                CreatedAt = DateTime.Now,
                Amount = amount,
                IsGrocery = isGroceryOrder
            };
            _context.Orders.Add(newOrder);
            await _context.SaveChangesAsync();
        }

      
        private int DetermineNewOrderNumber(Order currentHighestOrder, string extractedOrderNumber)
        {
            // Always correct the order number to be maxOrderNumber + 1
            if (currentHighestOrder != null && int.TryParse(currentHighestOrder.OrderNumber, out int highestOrderNumber))
            {
                return highestOrderNumber + 1;
            }
            return int.TryParse(extractedOrderNumber, out int parsedOrderNumber) ? parsedOrderNumber : 1;
        }

        // Method to send a message
        private async Task SendMessage(string recipient, string text)
        {
            var retryCount = 3;
            for (int attempt = 0; attempt < retryCount; attempt++)
            {
                try
                {
                    var client = _httpClientFactory.CreateClient();
                    var response = await client.PostAsync(
                        $"{_ultraMsgSettings.ApiBaseUrl.TrimEnd('/')}/{_ultraMsgSettings.InstanceId}/messages/chat",
                        new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                        { "token", _ultraMsgSettings.Token },
                        { "to", recipient },
                        { "body", text }
                        }));

                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("Message sent successfully.");
                        return;
                    }
                    else
                    {
                        Console.WriteLine($"Failed to send message. Status code: {response.StatusCode}");
                        Console.WriteLine($"Response content: {await response.Content.ReadAsStringAsync()}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Attempt {attempt + 1} failed: {ex.Message}");
                    if (attempt == retryCount - 1) throw;
                    await Task.Delay(2000); // Wait before retrying
                }
            }
        }

        // Method to parse the JSON and extract the "body" field
        private string ExtractMessageBody(string json)
        {
            try
            {
                var jsonObject = JsonConvert.DeserializeObject<dynamic>(json);
                return jsonObject?.data?.body;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"JSON parsing error: {ex.Message}");
                return null;
            }
        }

        // Existing methods for extraction
        private string ExtractOrderNumber(string message)
        {
            var lines = message.Split('\n');
            if (lines.Length > 0 && lines[0].Contains('-'))
            {
                var parts = lines[0].Split('-');
                if (parts.Length > 3)
                {
                    return parts[3].Trim();
                }
            }
            return null;
        }

        private decimal? ExtractAmount(string message)
        {
            var lines = message.Split('\n'); // Split message into lines to handle cases like "Total R1296"
            foreach (var line in lines)
            {
                // Check for a line that starts with "Total" to extract the amount in grocery orders
                if (line.StartsWith("Total", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(' ');
                    foreach (var part in parts)
                    {
                        if (part.StartsWith("R", StringComparison.OrdinalIgnoreCase) && decimal.TryParse(part.Substring(1), out var amount))
                        {
                            return amount;
                        }
                    }
                }

                // Also handle normal case where R followed by amount exists without "Total"
                var words = line.Split(' ');
                foreach (var word in words)
                {
                    if (word.StartsWith("R", StringComparison.OrdinalIgnoreCase) && decimal.TryParse(word.Substring(1), out var amount))
                    {
                        return amount;
                    }
                }
            }

            return null; // Return null if no amount is found
        }
        private decimal? ExtractTotalAmount(string message)
        {
            // Normalize spaces by replacing multiple spaces with a single space
            message = System.Text.RegularExpressions.Regex.Replace(message, @"\s+", " ");

            // Check for a line that contains "Total" followed by an amount
            var lines = message.Split('\n'); // Split message into lines to handle multiline messages
            foreach (var line in lines)
            {
                // Trim the line to handle extra spaces
                var trimmedLine = line.Trim();

                // Look for the keyword "Total" in each line (ignore case)
                if (trimmedLine.StartsWith("Total", StringComparison.OrdinalIgnoreCase))
                {
                    // Split the line into words and search for the amount prefixed with "R"
                    var parts = trimmedLine.Split(' ');
                    foreach (var part in parts)
                    {
                        // Check if part starts with "R" and is a valid decimal number
                        if (part.StartsWith("R", StringComparison.OrdinalIgnoreCase) && decimal.TryParse(part.Substring(1), out var totalAmount))
                        {
                            return totalAmount; // Return the extracted total amount
                        }
                    }
                }
            }

            // If no valid amount found, return null
            return null;
        }

        private decimal? ExtractTotalAmount1(string message)
        {
            // Check for a line that contains "Total" followed by an amount
            var lines = message.Split('\n'); // Split message into lines to handle multiline messages
            foreach (var line in lines)
            {
                // Look for the keyword "Total" in each line
                if (line.StartsWith("Total", StringComparison.OrdinalIgnoreCase))
                {
                    // Split the line into words and search for the amount prefixed with "R"
                    var parts = line.Split(' ');
                    foreach (var part in parts)
                    {
                        // Check if part starts with "R" and is a valid decimal number
                        if (part.StartsWith("R", StringComparison.OrdinalIgnoreCase) && decimal.TryParse(part.Substring(1), out var totalAmount))
                        {
                            return totalAmount; // Return the extracted total amount
                        }
                    }
                }
            }

            // If no valid amount found, return null
            return null;
        }


        private bool IsGroceryOrder(string message)
        {
            // Check if "Total" exists or any pattern that indicates it's a grocery order
            if (message.Contains("Total", StringComparison.OrdinalIgnoreCase) || message.Contains("*Groceries for*", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Otherwise, it's likely not a grocery order
            return false;
        }

        private string ExtractBranch(string message)
        {
            var lines = message.Split('\n');
            if (lines.Length > 2)
            {
                return lines[2].Trim();
            }
            return null;
        }

        private string ExtractRecipient(string message)
        {
            var lines = message.Split('\n');
            if (lines.Length > 1)
            {
                return lines[1].Replace("to ", "").Trim();
            }
            return "Unknown Recipient";
        }
    }
}
