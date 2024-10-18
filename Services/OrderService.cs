using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using OrderVerificationAPI.Interfaces;
using OrderVerificationAPI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

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

            // Check if the message contains a "ProcessedByService" tag to prevent processing outgoing messages
            if (body.Contains("[MWZ~ChatBotService]"))
            {
                Console.WriteLine("Skipping message already processed by the service.");
                return ""; // Skip processing this message
            }

            // Extract order number, amount, branch, and recipient from the message body
            var extractedOrderNumber = ExtractOrderNumber(body);
            var extractedAmount = ExtractAmount(body);
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

            // Determine new order number
            var currentHighestOrder = await _context.Orders.OrderByDescending(o => o.OrderNumber).FirstOrDefaultAsync();
            int newOrderNumber = DetermineNewOrderNumber(currentHighestOrder, extractedOrderNumber);

            // Construct a complete order message with a tag to avoid re-processing
            var completeMessage = CreateDetailedOrderMessage(newOrderNumber.ToString(), body, matchedBranch.Name, recipient, extractedAmount.Value) + " [MWZ~ChatBotService]";

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
                await CreateNewOrder(newOrderNumber.ToString(), matchedBranch.BranchId, recipient, extractedAmount.Value);

                // Notify the sender and the branch with the corrected order number
                await SendMessage(sender, $"Duplicate order detected. Corrected order number: {newOrderNumber}");
                await SendMessage(matchedBranch.PhoneNumber, completeMessage);
                await SendMessage(matchedBranch.AdminPhoneNumber, completeMessage);

                return newOrderNumber.ToString();
            }

            // If no duplicate order, create a new one
            await CreateNewOrder(extractedOrderNumber, matchedBranch.BranchId, recipient, extractedAmount.Value);

            // Notify the branch and admin with the complete message
            await SendMessage(matchedBranch.PhoneNumber, completeMessage);
            await SendMessage(matchedBranch.AdminPhoneNumber, completeMessage);

            Console.WriteLine("Order processed successfully.");
            return "Order processed successfully.";
        }

        public async Task<string> VerifyAndProcessOrder1(string orderNumber, string sender, string jsonBody)
        {
            Console.WriteLine($"VerifyAndProcessOrder called with orderNumber: {orderNumber}, sender: {sender}");

            // Check if the sender is allowed
            if (!allowedSenders.Contains(sender))
            {
                //Console.WriteLine("Sender is not authorized.");
                //await SendMessage(sender, "Error: You are not authorized to send orders.");
                return "";// "Unauthorized sender.";
            }

            // Parse the JSON to extract the "body" field
            var body = ExtractMessageBody(jsonBody);
            if (string.IsNullOrEmpty(body))
            {
                Console.WriteLine("Failed to extract message body from the JSON.");
                //await SendMessage(sender, "Error: Unable to extract message body. Please check the message format.");
                return ""; //Error in message format.";
            }

            // Verify that the message starts with "ID-"
            if (!body.StartsWith("ID-", StringComparison.OrdinalIgnoreCase))
            {
                //Console.WriteLine("Message does not start with 'ID-'.");
                //await SendMessage(sender, "Error: Invalid message format. The message should start with 'ID-'.");
                return "";
            }

            // Extract order number, amount, and branch from the body
            var extractedOrderNumber = ExtractOrderNumber(body);
            var extractedAmount = ExtractAmount(body);
            var extractedBranchName = ExtractBranch(body);

            if (string.IsNullOrEmpty(extractedOrderNumber) || extractedAmount == null || string.IsNullOrEmpty(extractedBranchName))
            {
                Console.WriteLine("Failed to extract order number, amount, or branch from the message.");
                //await SendMessage(sender, "Error: Unable to extract order number, amount, or branch. Please check the message format.");
                return "";// Error in message format.";
            }

            // Retrieve branches from the database and find the matching branch
            var branches = await _context.Branches.ToListAsync();
            var matchedBranch = branches.FirstOrDefault(branch => body.Contains(branch.Name, StringComparison.OrdinalIgnoreCase));

            if (matchedBranch == null)
            {
                Console.WriteLine("No matching branch found in the message.");
                await SendMessage(sender, "Error: Branch not found in the message.");
                return "Branch not found.";
            }
            Console.WriteLine($"Matched Branch: {matchedBranch.Name}");

            var currentHighestOrder = await _context.Orders.OrderByDescending(o => o.OrderNumber).FirstOrDefaultAsync();

            int newOrderNumber;
            if (currentHighestOrder != null && int.TryParse(currentHighestOrder.OrderNumber, out int highestOrderNumber))
            {
                if (int.TryParse(extractedOrderNumber, out int extractedOrderInt) && extractedOrderInt <= highestOrderNumber)
                {
                    newOrderNumber = highestOrderNumber + 1;
                    Console.WriteLine($"Order number already exists or is less than current highest. New order number generated: {newOrderNumber}");
                }
                else
                {
                    newOrderNumber = extractedOrderInt;
                }
            }
            else
            {
                newOrderNumber = int.TryParse(extractedOrderNumber, out int parsedOrderNumber) ? parsedOrderNumber : 1;
            }

            var correctedMessage = CorrectOrderNumber(newOrderNumber.ToString(), body);

            //// Check if this order needs to be created in the database
            //var existingOrder = await _context.Orders.FirstOrDefaultAsync(o => o.OrderNumber == extractedOrderNumber);
            //if (existingOrder != null)
            //{
            //    await SendMessage(sender, $"Duplicate order detected. Corrected order number: {newOrderNumber}");
            //    await SendMessage(matchedBranch.PhoneNumber, correctedMessage);
            //    await SendMessage(matchedBranch.AdminPhoneNumber, correctedMessage);

            //    return newOrderNumber.ToString();
            //}
            // Check if this order needs to be created in the database
            var existingOrder = await _context.Orders.FirstOrDefaultAsync(o => o.OrderNumber == extractedOrderNumber);
            if (existingOrder != null)
            {
                // Duplicate order detected
                Console.WriteLine("Duplicate order detected, generating a new order number.");

                // Update to use the new order number
                var correctedOrderNumber = newOrderNumber.ToString();
                correctedMessage = CorrectOrderNumber(correctedOrderNumber, body); // Reuse the existing variable

                // Create a new order with the corrected number
                var duplicateOrder = new Order
                {
                    OrderNumber = correctedOrderNumber,
                    BranchId = matchedBranch.BranchId,
                    Recipient = ExtractRecipient(body),
                    Status = "New",
                    CreatedAt = DateTime.Now,
                    Amount = extractedAmount.Value
                };
                _context.Orders.Add(duplicateOrder);
                await _context.SaveChangesAsync();

                // Notify the sender and the branch about the corrected order
                await SendMessage(sender, $"Duplicate order detected. Corrected order number: {correctedOrderNumber}");
                await SendMessage(matchedBranch.PhoneNumber, correctedMessage);
                await SendMessage(matchedBranch.AdminPhoneNumber, correctedMessage);

                return correctedOrderNumber;
            }
            // If the order does not exist, proceed to create it
            Console.WriteLine("Creating a new order.");
            var newOrder = new Order
            {
                OrderNumber = extractedOrderNumber,
                BranchId = matchedBranch.BranchId,
                Recipient = ExtractRecipient(body),
                Status = "New",
                CreatedAt = DateTime.Now,
                Amount = extractedAmount.Value
            };
            _context.Orders.Add(newOrder);
            await _context.SaveChangesAsync();

            //await SendMessage(sender, "Order processed successfully.");
            await SendMessage(matchedBranch.PhoneNumber, body);
            await SendMessage(matchedBranch.AdminPhoneNumber, body);

            Console.WriteLine("Order processed successfully.");
            return "Order processed successfully.";
        }


        private int DetermineNewOrderNumber(Order currentHighestOrder, string extractedOrderNumber)
        {
            if (currentHighestOrder != null && int.TryParse(currentHighestOrder.OrderNumber, out int highestOrderNumber))
            {
                if (int.TryParse(extractedOrderNumber, out int extractedOrderInt) && extractedOrderInt <= highestOrderNumber)
                {
                    return highestOrderNumber + 1;
                }
                return extractedOrderInt;
            }
            return int.TryParse(extractedOrderNumber, out int parsedOrderNumber) ? parsedOrderNumber : 1;
        }

        private async Task HandleDuplicateOrder(string sender, Branch matchedBranch, string detailedMessage, int newOrderNumber)
        {
            // Notify about the duplicate order
            await SendMessage(sender, $"Duplicate order detected. Corrected order number: {newOrderNumber}");
            await SendMessage(matchedBranch.PhoneNumber, detailedMessage);
            await SendMessage(matchedBranch.AdminPhoneNumber, detailedMessage);
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

        private async Task SendMessage(string recipient, string text)
        {
            var retryCount = 3;
            for (int attempt = 0; attempt < retryCount; attempt++)
            {
                try
                {
                    Console.WriteLine($"Sending message to {recipient}: {text}");
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
                        return; // Exit the method if the message was sent successfully
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
                    if (attempt == retryCount - 1) throw; // If the last attempt fails, rethrow the exception
                    await Task.Delay(2000); // Wait before retrying
                }
            }
        }

        private string GenerateNewOrderNumber()
        {
            return $"ORD-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}";
        }

        private string CorrectOrderNumber(string newOrderNumber, string originalMessage)
        {
            // Replace the old order number in the message with the new one
            var parts = originalMessage.Split('-');
            if (parts.Length > 3)
            {
                parts[3] = newOrderNumber;
                return string.Join('-', parts);
            }
            return originalMessage;
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
            var parts = message.Split(' ');
            foreach (var part in parts)
            {
                if (part.StartsWith("R", StringComparison.OrdinalIgnoreCase) && decimal.TryParse(part.Substring(1), out var amount))
                {
                    return amount;
                }
            }
            return null;
        }
        private string CreateDetailedOrderMessage(string orderNumber, string originalMessage, string branch, string recipient, decimal amount)
        {
            // Correct the order number in the original message
            var correctedMessage = CorrectOrderNumber(orderNumber, originalMessage);

            // Construct the complete message with the corrected order details
            return $"{correctedMessage}\n" +
                   $"to {recipient}\n" +
                   $"{branch} " +
                   $"NJ Sales R{amount}";
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
