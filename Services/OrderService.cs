using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using OrderVerificationAPI.Interfaces;
using OrderVerificationAPI.Models;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace OrderVerificationAPI.Services
{
    public class OrderService : IOrderService
    {
        private readonly AppDbContext _context;
        private readonly UltraMsgSettings _ultraMsgSettings;
        private readonly IHttpClientFactory _httpClientFactory;
        private static readonly object _processOrderLock = new object();
        private readonly List<string> allowedSenders = new List<string>
        {
            "27746262742@c.us" // Add more allowed numbers here
        };
        //public OrderService(AppDbContext context, IOptions<UltraMsgSettings> ultraMsgSettings, IHttpClientFactory httpClientFactory)
        //{
        //    _context = context;
        //    _ultraMsgSettings = ultraMsgSettings.Value;
        //    _httpClientFactory = httpClientFactory;
        //}
        // Concurrent dictionary to track recently processed orders
        private static readonly ConcurrentDictionary<string, DateTime> _recentlyProcessedOrders = new ConcurrentDictionary<string, DateTime>();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

        public OrderService(AppDbContext context, IOptions<UltraMsgSettings> ultraMsgSettings, IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _ultraMsgSettings = ultraMsgSettings.Value;
            _httpClientFactory = httpClientFactory;
        }
        public async Task<string> VerifyAndProcessOrder(string orderNumber, string sender, string jsonBody)
        {
         
           Console.WriteLine($"VerifyAndProcessOrder called with orderNumber: {orderNumber}, sender: {sender}");

            if (!allowedSenders.Contains(sender)) return ""; // Unauthorized sender

            var body = ExtractMessageBody(jsonBody);
            if (string.IsNullOrEmpty(body) || !body.StartsWith("ID-", StringComparison.OrdinalIgnoreCase)) return ""; // Invalid format

            if (body.Contains("[MWZ~ChatBotService]"))
            {
                Console.WriteLine("Skipping message already processed by the service.");
                return "";
            }
            lock (_processOrderLock)
            {
                // Check if this order has already been processed recently
                if (_recentlyProcessedOrders.TryGetValue(orderNumber, out var lastProcessedTime) &&
                    (DateTime.Now - lastProcessedTime) < CacheDuration)
                {
                    Console.WriteLine("Order recently processed, skipping.");
                    return ""; // Skip since the order was processed already
                }

                // Mark the order as being processed
                _recentlyProcessedOrders[orderNumber] = DateTime.Now;
            }
            try
            {
                if (_recentlyProcessedOrders.TryGetValue(orderNumber, out var lastProcessedTime) &&
                    (DateTime.Now - lastProcessedTime) < CacheDuration)
                {
                    Console.WriteLine("Order recently processed, skipping.");
                    return ""; // Already processed
                }
                var branches = await _context.Branches.ToListAsync();
                bool isGroceryOrder = IsGroceryOrder(body);
                var extractedOrderNumber = ExtractOrderNumber(body);
                var extractedAmount = isGroceryOrder ? ExtractTotalAmount(body) : ExtractAmount(body);
                var extractedBranchName = ExtractBranch(body,branches);//(body);
                var recipient = ExtractRecipient(body);

                if (string.IsNullOrEmpty(extractedOrderNumber) || extractedAmount == null || string.IsNullOrEmpty(extractedBranchName.Name))
                {
                    Console.WriteLine("Failed to extract order number, amount, or branch from the message.");
                    return ""; // Invalid message content
                }


                // var matchedBranch = branches.FirstOrDefault(branch => body.Contains(branch.Name, StringComparison.OrdinalIgnoreCase));
                var matchedBranch = branches
                                   .OrderByDescending(branch => branch.Name.Length) // Sort by length of branch name to prioritize longer names
                                   .FirstOrDefault(branch => body.Contains(branch.Name, StringComparison.OrdinalIgnoreCase));

                if (matchedBranch == null) return "Branch not found.";

                var maxOrder = await _context.Orders
                               .Include(o => o.Branch)
                               .Where(o => o.Branch.Group == matchedBranch.Group)
                               .OrderByDescending(o => o.OrderNumber)
                               .FirstOrDefaultAsync();
                int newOrderNumber = DetermineNewOrderNumber(maxOrder, extractedOrderNumber);
                if (maxOrder != null)
                {
                    if (int.TryParse(extractedOrderNumber, out int extractedOrderInt) && extractedOrderInt <= int.Parse(maxOrder.OrderNumber))
                    {
                        newOrderNumber = int.Parse(maxOrder.OrderNumber) + 1;
                        Console.WriteLine($"Order number outdated, correcting to: {newOrderNumber}");
                    }
                }
                var completeMessage = $"{body} [MWZ~ChatBotService]";

                var existingOrder = await _context.Orders
                    .Where(o => o.OrderNumber == extractedOrderNumber && o.Branch.Group == matchedBranch.Group)
                    .FirstOrDefaultAsync();
                if (existingOrder != null)
                {
                    if (existingOrder.OrderNumber == newOrderNumber.ToString())
                    {
                        Console.WriteLine("Order already corrected, skipping further processing.");
                        return newOrderNumber.ToString();
                    }

                    await CreateNewOrder(newOrderNumber.ToString(), matchedBranch.BranchId, recipient, extractedAmount.Value, isGroceryOrder);

                    await SendMessage(sender, $"Duplicate order detected. Corrected order number: {newOrderNumber}");
                    //await SendMessage(matchedBranch.PhoneNumber, completeMessage.Replace(extractedOrderNumber, newOrderNumber.ToString()));
                    //await SendMessage(matchedBranch.AdminPhoneNumber, completeMessage.Replace(extractedOrderNumber, newOrderNumber.ToString()));

                    _recentlyProcessedOrders[orderNumber] = DateTime.Now;

                    return newOrderNumber.ToString();
                }

                await CreateNewOrder(extractedOrderNumber, matchedBranch.BranchId, recipient, extractedAmount.Value, isGroceryOrder);

                //await SendMessage(matchedBranch.PhoneNumber, completeMessage);
                //await SendMessage(matchedBranch.AdminPhoneNumber, completeMessage);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Processing error: {ex.Message}");
            }
            finally
            {
                Dispose();
            }
            Console.WriteLine("Order processed successfully.");
            _recentlyProcessedOrders[orderNumber] = DateTime.Now;
            return "Order processed successfully.";

        }

        public void Dispose()
        {
            _context?.Dispose();
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

        public async Task SendMessage(string recipient, string text)
        {
            if (string.IsNullOrWhiteSpace(recipient))
            {
                Console.WriteLine("Recipient number is empty, skipping message sending.");
                return;
            }

            try
            {
                if (recipient.ToString() != "0" )
   
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
                    }
                    else
                    {
                        Console.WriteLine($"Failed to send message. Status code: {response.StatusCode}");
                        Console.WriteLine($"Response content: {await response.Content.ReadAsStringAsync()}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error while sending message: {ex.Message}");
            }
        }

        public async Task SendMessage1(string recipient, string text)
        {
            if (string.IsNullOrWhiteSpace(recipient))
            {
                Console.WriteLine("Recipient number is empty, skipping message sending.");
                return;
            }

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
            //message = System.Text.RegularExpressions.Regex.Replace(message, @"\s+", " ");

            // Check for a line that contains "Total" followed by an amount
            var lines = message.Split('\n'); // Split message into lines to handle multiline messages
            foreach (var line in lines)
            {
                // Trim the line to handle extra spaces
                var trimmedLine = line.Trim();

                // Look for the keyword "Total" in each line (ignore case)
                if (trimmedLine.StartsWith("Total", StringComparison.OrdinalIgnoreCase) || trimmedLine.Contains("roceries for", StringComparison.OrdinalIgnoreCase))
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

        private Branch ExtractBranch(string message, List<Branch> branches)
        {
            Branch bestMatch = null;
            branches=branches.OrderByDescending(branch => branch.BranchId).ToList();
            // Normalize message by removing excess spaces and combining lines with a separator
            string normalizedMessage = string.Join(" ", message.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));

            foreach (var branch in branches)
            {
                var pattern = $@"\b{Regex.Escape(branch.Name)}\b";
                if (Regex.IsMatch(normalizedMessage, pattern, RegexOptions.IgnoreCase))
                {
                    // If there's no best match yet or the current branch name is longer, update bestMatch
                    if (bestMatch == null || branch.Name.Length > bestMatch.Name.Length)
                    {
                        bestMatch = branch;
                    }
                }
            }

            return bestMatch;
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
        #region Handle auto responses
        // New method to handle keyword-based responses
        public async Task<string> HandleKeywordBasedResponses(string sender, string message)
        {
            //await FetchAndStoreGroupIds();
            // Normalize the message for comparison by converting to lowercase
            message = message.ToLower();

            // List of keyword-based triggers and their corresponding responses
            var keywordResponses = new List<(List<string> keywords, string response)>
            {
                (new List<string> { "ndoda account", "ndokumbirawo account","ndokumbirawo acc", "ndipeiwo account", "account number yakarasika","FNB","YeCapitec","account yenyu",
                    "ndipei account", "can i have account","ndaikumbirawo account", "account yenyu irkushanda here" },
                    "*MWZ Diaspora Services Banking Details*\n\n" +
                    "Mari(Cash) 10%\nGroceries and Hardware 5%\n\n" +
                    "*Mari dziripasi peR1000(Less than R1000)*\n" +
                    "FNB (Less than 1000)\nAccount number 62844699770\nA Zimusi\n\n" +
                    "*Mari dziripamusoro peR1000(More than R1000)*\n" +
                    "FNB 62927338807\nMWZ Diaspora Services\n\n" +
                    "*Chero Mari(Any Amount)*\nCapitec\nA Zimusi\n2275313199\n\n" +
                    "(Transfers from Nedbank only/ Vanotumira kubva kunedbank neaccount chete)\n" +
                    "Nedbank\nA. Zimusi\n1284377113\n\n" +
                    "*MWZ Diaspora Services : 0746262742*\n\n" +
                    "T & C *Mari munoisa paATM or kuita transfer mukaisa mukati mebank munobhadhara macharges*"),

                (new List<string> { "mamuka sei" }, "Tamuka mamukawo sei"),
                (new List<string> { "mamka sei" }, "Tamuka mamukawo sei"),
                (new List<string> { "dakutumira mari" }, "Tumirai henyu"),
                (new List<string> { "ndichatumira mari" }, "Tumirai henyu"),
                (new List<string> { "ndoda kutumira mari" }, "Tumirai henyu"),
                (new List<string> { "kurisei sei" }, "kwakanaka uku murisei"),
                (new List<string> { "murisei" }, "Tiripo makadiniwo"),
                (new List<string> { "kurisei" }, "Kuribhoo murisei"),
                (new List<string> { "makadini" }, "Tiripo makadiniwo"),
                (new List<string> { "kwaziwai" }, "Tiripo makadiniwo"),
               // (new List<string> { "hi" }, "Hi"),
                (new List<string> { "sekuru murisei" }, "Kuribhoo uku mzaya, kurisei"),
                (new List<string> { "kule murisei" }, "Kuribhoo uku mzaya, kurisei"),
                (new List<string> { "mzaya murisei" }, "Kuribhoo uku kule, murisei"),
                (new List<string> { "gules murisei" }, "Kuribhoo uku murisei bamnini"),
                (new List<string> { "kukanda mari" }, "Kandai henyu"),
                (new List<string> { "ndirikuda kutumira" }, "Tumirai henyu"),
                (new List<string> { "mamuka here" }, "Tamuka mamukawo sei"),
                (new List<string> { "hello" }, "Hello"),
                (new List<string> { "maita basa" }, "Zvakanakai"),
                (new List<string> { "thanks" }, "Zvakanakai"),
                (new List<string> { "dankie" }, "Zvakanakai tinotenda"),
                (new List<string> { "danki" }, "Zvakanakai tinotenda"),

                (new List<string> { "thank you" }, "Zvakanakai tinotenda"),
                (new List<string> { "maswera sei", "maswera here", "maswera" }, "Taswera maswerawo"),
                (new List<string> { "ndoda kutumira mari", "ndokumbirawo kutumira mari", "ndoda kusenda mari",
                    "ndokumbira kusenda mari", "ndirikutumira mari", "ndodawo kutumira mari", "ndingatumirawo mari",
                    "ndingasendawo mari" }, "Tumirai henyu")
            };
           
            // Loop through the keyword-responses list and find a match
            foreach (var (keywords, response) in keywordResponses)
            {
                // If any of the keywords are contained in the message
                if (keywords.Any(keyword => message.Contains(keyword)))
                {
                    // Send the appropriate response to the sender with a unique tag
                    await SendMessage(sender, $"{response}\n\n");// [MWZ~ChatBotResponse]");
                    return response; // Return the response that was sent
                }
            }
            return null; // No keyword matched
        }
        public async Task<string> HandleKeywordBasedResponses1(string sender, string message)
        {
            // Normalize the message for comparison by converting to lowercase
            message = message.ToLower();

            // List of keyword-based triggers and their corresponding responses
            var keywordResponses = new List<(List<string> keywords, string response)>
            {
                (new List<string> { "ndoda account", "ndokumbirawo account", "ndipeiwo account", "account number yakarasika","FNB","YeCapitec","account yenyu",
                    "ndipei account", "can i have account","ndaikumbirawo account", "account yenyu irkushanda here" },
                    "*MWZ Diaspora Services Banking Details*\n\n" +
                    "Mari(Cash) 10%\nGroceries and Hardware 5%\n\n" +
                    "*Mari dziripasi peR1000(Less than R1000)*\n" +
                    "FNB (Less than 1000)\nAccount number 62844699770\nA Zimusi\n\n" +
                    "*Mari dziripamusoro peR1000(More than R1000)*\n" +
                    "FNB 62927338807\nMWZ Diaspora Services\n\n" +
                    "*Chero Mari(Any Amount)*\nCapitec\nA Zimusi\n2275313199\n\n" +
                    "(Transfers from Nedbank only/ Vanotumira kubva kunedbank neaccount chete)\n" +
                    "Nedbank\nA. Zimusi\n1284377113\n\n" +
                    "*MWZ Diaspora Services : 0746262742*\n\n" +
                    "T & C *Mari munoisa paATM or kuita transfer mukaisa mukati mebank munobhadhara macharges*"),

                (new List<string> { "mamuka sei" }, "Tamuka mamukawo sei"),
                (new List<string> { "mamka sei" }, "Tamuka mamukawo sei"),
                (new List<string> { "dakutumira mari" }, "Tumirai henyu"),
                (new List<string> { "ndichatumira mari" }, "Tumirai henyu"),
                (new List<string> { "ndoda kutumira mari" }, "Tumirai henyu"),
                (new List<string> { "kurisei sei" }, "kwakanaka uku murisei"),
                (new List<string> { "murisei" }, "Tiripo makadiniwo"),
                (new List<string> { "kurisei" }, "Kuribhoo murisei"),
                (new List<string> { "makadini" }, "Tiripo makadiniwo"),
                (new List<string> { "kwaziwai" }, "Tiripo makadiniwo"),
                (new List<string> { "hi" }, "Hi"),
                (new List<string> { "sekuru murisei" }, "Kuribhoo uku mzaya, kurisei"),
                (new List<string> { "kule murisei" }, "Kuribhoo uku mzaya, kurisei"),
                (new List<string> { "mzaya murisei" }, "Kuribhoo uku kule, murisei"),
                (new List<string> { "gules murisei" }, "Kuribhoo uku murisei bamnini"),
                (new List<string> { "kukanda mari" }, "Kandai henyu"),

                (new List<string> { "mamuka here" }, "Tamuka mamukawo sei"),
                (new List<string> { "hello" }, "Hello"),
                (new List<string> { "maita basa" }, "Zvakanakai tinotenda nekushandidzana kwakanaka"),
                (new List<string> { "thanks" }, "Zvakanakai tinotenda"),
                (new List<string> { "dankie" }, "Zvakanakai tinotenda"),
                (new List<string> { "danki" }, "Zvakanakai tinotenda"),

                (new List<string> { "thank you" }, "Zvakanakai tinotenda"),
                (new List<string> { "maswera sei", "maswera here", "maswera" }, "Taswera maswerawo"),
                (new List<string> { "ndoda kutumira mari", "ndokumbirawo kutumira mari", "ndoda kusenda mari",
                    "ndokumbira kusenda mari", "ndirikutumira mari", "ndodawo kutumira mari", "ndingatumirawo mari",
                    "ndingasendawo mari" }, "Tumirai henyu")
            };

            // Loop through the keyword-responses list and find a match
            //foreach (var (keywords, response) in keywordResponses)
            //{
            //    // If any of the keywords are contained in the message
            //    if (keywords.Any(keyword => message.Contains(keyword)))
            //    {
            //        // Send the appropriate response to the sender
            //        await SendMessage(sender, response);
            //        return response; // Return the response that was sent
            //    }
            //}
            // Loop through the keyword-responses list and find a match
            foreach (var (keywords, response) in keywordResponses)
            {
                // If any of the keywords are contained in the message
                if (keywords.Any(keyword => message.Contains(keyword)))
                {
                    // Send the appropriate response to the sender
                    await SendMessage(sender, response); // Ensure 'sender' is the correct WhatsApp ID here
                    return response; // Return the response that was sent
                }
            }
            return null; // No keyword matched
        }
        public bool IsMessageRecent(DateTime messageTime, int thresholdMinutes = 10)
        {
            DateTime currentTime = DateTime.Now; // This gets the current local time
            TimeSpan timeDifference = currentTime - messageTime;

            return timeDifference.TotalMinutes <= thresholdMinutes;
        }
        public DateTime ConvertUnixTimestampToDateTime(long unixTimestamp)
        {
            // Convert Unix timestamp to UTC DateTime
            DateTime dateTimeUtc = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime;

            // Adjust to South Africa Standard Time (UTC+2)
            TimeZoneInfo southAfricaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time");
            DateTime southAfricaDateTime = TimeZoneInfo.ConvertTimeFromUtc(dateTimeUtc, southAfricaTimeZone);

            return southAfricaDateTime;
        }
        // Call this method in the Webhook or any message handler that receives messages
        public async Task<string> ProcessAndRespondToMessage(string sender, string message)
        {
            // Check for keyword-based responses
            var response = await HandleKeywordBasedResponses(sender, message);
            if (response != null)
            {
                // If a response was sent based on a keyword, return success
                return "Keyword-based response sent.";
            }

            // If no keyword-based response, continue with other processing (like orders) if necessary
            // (Existing order processing logic goes here...)

            return "No keyword-based response required.";
        }
        #endregion
        public async Task<List<ChatInfo>> FetchAndStoreGroupIds()
        {
            var client = _httpClientFactory.CreateClient();
            var groupIds = new List<ChatInfo>();

            try
            {
                // Call UltraMsg API to get all active chats
                var response = await client.GetAsync(
                    $"{_ultraMsgSettings.ApiBaseUrl.TrimEnd('/')}/{_ultraMsgSettings.InstanceId}/getChats?token={_ultraMsgSettings.Token}");

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();

                    // Attempt to deserialize as List<ChatInfo>
                    try
                    {
                        var chatList = JsonConvert.DeserializeObject<List<ChatInfo>>(responseContent);

                        // Filter out groups
                        groupIds = chatList.Where(chat => chat.Id.EndsWith("@g.us")).ToList();

                        // Log and output the group IDs (just for verification)
                        Console.WriteLine("Fetched Group IDs:");
                        foreach (var group in groupIds)
                        {
                            Console.WriteLine($"Group Name: {group.Name}, Group ID: {group.Id}");
                        }

                        // Store or save the group IDs as needed, e.g., in a database or a configuration file.
                        // For demonstration, this could be saved into a local file.
                        var groupData = JsonConvert.SerializeObject(groupIds);
                        await File.WriteAllTextAsync("GroupIds.json", groupData);
                    }
                    catch (JsonSerializationException)
                    {
                        // Handle error response
                        //var errorResponse = JsonConvert.DeserializeObject<ErrorResponse>(responseContent);
                        //Console.WriteLine($"Error fetching group IDs: {errorResponse?.Error}");
                    }
                }
                else
                {
                    Console.WriteLine($"Failed to fetch chats. Status code: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching group IDs: {ex.Message}");
            }

            return groupIds;
        }


    }
}