using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using OrderVerificationAPI.Interfaces;
using OrderVerificationAPI.Models;
using OrderVerificationAPI.Models.OrderVerificationAPI.Models;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Transactions;
using Tesseract;

namespace OrderVerificationAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WebhookController : ControllerBase
    {
        private readonly IOrderService _orderService;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);
        private static readonly string ServiceSenderId = "27746262742@c.us";
        private static readonly ConcurrentDictionary<string, DateTime> ProcessedMessageIds = new ConcurrentDictionary<string, DateTime>();
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AppDbContext _context;

        public WebhookController(IOrderService orderService, IHttpClientFactory httpClientFactory, AppDbContext context)
        {
            _orderService = orderService;
            _httpClientFactory = httpClientFactory;
            _context = context;
        }

        [HttpPost]
        public async Task<IActionResult> ReceiveMessage()
        {
            try
            {
                using (var reader = new StreamReader(Request.Body))
                {
                    var body = await reader.ReadToEndAsync();
                    Console.WriteLine($"Received JSON: {body}");

                    // Deserialize JSON into UltraMsgWebhook object
                    var webhook = JsonConvert.DeserializeObject<UltraMsgWebhook>(body);
                    if (webhook == null || webhook.Data == null)
                    {
                        Console.WriteLine("Invalid webhook data received");
                        return BadRequest("Invalid webhook format.");
                    }

                    // Extract message details and a unique message ID
                    string text = webhook.Data.Body;
                    string sender = webhook.Data.From;
                    string messageId = webhook.Data.Id;
                    string messageType = webhook.Data.Type;
                    string mediaUrl = webhook.Data.Media;
                    string To = webhook.Data.To;
                    var messageTimestamp = DateTimeOffset.FromUnixTimeSeconds(webhook.Data.Time).UtcDateTime;

                    Console.WriteLine($"Parsed message: Text={text}, Sender={sender}, MessageId={messageId}, Timestamp={messageTimestamp}");

                    // Check if the message is within a recent time window (e.g., 5 minutes)
                    if (webhook.Data?.Time != null)
                    {
                        DateTime messageDateTime = _orderService.ConvertUnixTimestampToDateTime(webhook.Data.Time);

                        if (!_orderService.IsMessageRecent(messageDateTime))
                        {
                            Console.WriteLine("Ignoring old message.");
                            return Ok("Old message ignored.");
                        }
                    }
                    else
                    {
                        Console.WriteLine("Time field missing or invalid in webhook data.");
                        return BadRequest("Invalid message time.");
                    }

                    // Proceed with existing checks and logic for valid messages
                    if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(messageId))
                    {
                        if (!(messageType == "image"))
                        {
                            Console.WriteLine("Message content or ID is missing or invalid");
                            return BadRequest("Invalid message content.");
                        }
                    }

                    // Ignore messages from the service sender if they are not order-related
                    if (sender == ServiceSenderId && !text.StartsWith("ID-", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("Ignoring non-order message from service sender.");
                        return Ok("Non-order message from service sender ignored.");
                    }

                    // Check if the message has been processed within the cache duration
                    if (ProcessedMessageIds.ContainsKey(messageId) && (DateTime.Now - ProcessedMessageIds[messageId]) < CacheDuration)
                    {
                        Console.WriteLine("Message already processed, skipping.");
                        return Ok("Duplicate message ignored.");
                    }

                    // Add message ID to the processed cache
                    ProcessedMessageIds[messageId] = DateTime.Now;

                    // Handle image message
                    if (messageType == "image" && !string.IsNullOrEmpty(mediaUrl))
                    {
                        if (To.Contains("@g.us") && sender == ServiceSenderId)
                        {
                            Console.WriteLine("Ignored outgoing message to group.");
                            return Ok();
                        }
                        if (To.Contains("777202850") || To.Contains("713978760") || To.Contains("772727946") || To.Contains("779052292"))
                        {
                            Console.WriteLine("Ignored images from business partners");
                            return Ok();
                        }
                        if (sender.Contains(ServiceSenderId))
                        {
                            Console.WriteLine("Ignored outgoing image from service.");
                            return Ok("Outgoing service backup");
                        }
                        var imagePath = await DownloadImage(mediaUrl);

                        // Extract text from the image
                        var extractedText = ExtractTextFromImage(imagePath);
                        if (!string.IsNullOrWhiteSpace(extractedText))
                        {
                            var transactionCodes = ExtractTransactionCodes(extractedText);

                            // Handle cases with multiple transaction codes
                            if (transactionCodes.Count > 1)
                            {
                                await _orderService.SendMessage(sender, $"AI yedu irikuti matumira maslip anopfuura 1 papicture imwe chete. Tumirai 1 slip papicture imwe");
                                Console.WriteLine("Multiple transaction codes detected, possible multiple slips.");
                                return Ok("Multiple transaction codes detected. Please send separate images for each slip.");
                            }
                            else if (transactionCodes.Count == 1)
                            {
                                var transactionCode = transactionCodes.First();

                                // Check if the transaction code is unique, but allow resubmission from the same sender within 24 hours
                                if (IsTransactionCodeDuplicateForSender(transactionCode, sender))
                                {
                                    Console.WriteLine("Duplicate transaction code detected from the same sender within 24 hours, ignoring.");
                                    ClearImage(imagePath);
                                    return Ok("Duplicate transaction code ignored from the same sender.");
                                }

                                if (!IsTransactionCodeDuplicate(transactionCode))
                                {
                                    // Save transaction code along with sender info
                                    SaveTransactionCode(transactionCode, sender);
                                    ClearImage(imagePath);
                                    return Ok("Transaction code saved.");
                                }
                                else
                                {
                                    await _orderService.SendMessage(sender, $"System yedu irikuti picture iyi yambotumirwa, musatumira picture imwe chete kaviri asi kana ari agent wedu ati sendai zvakare no problem");
                                    Console.WriteLine("Duplicate transaction code detected.");
                                    ClearImage(imagePath);
                                    return Ok("Duplicate transaction code detected.");
                                }
                            }
                            else
                            {
                                // No valid transaction code found in the image
                                await _orderService.SendMessage(sender, $"AI yedu irikuti hatina kuona Trace ID kana Transaction ID pa picture yenyu. Ndokumbirawo muve nechokwadi kuti masendapicture yacho zvakanaka ichioneka pane Date, Account Number, Trace ID kana Transaction ID zvichioneka tiwane kukubatsirai. Iyi message yabva kusystem yedu inoongorora maslips");
                                Console.WriteLine("No valid transaction code detected in the image.");
                                ClearImage(imagePath);
                                return Ok("No valid transaction code found, customer notified.");
                            }
                        }
                        else
                        {
                            // Failed to extract any text from the image
                            await _orderService.SendMessage(sender, $"AI yedu irikuti hatina kuona Trace ID kana Transaction ID pa picture yenyu. Ndokumbirawo muve nechokwadi kuti masendapicture yacho zvakanaka ichioneka pane Date, Account Number, Trace ID kana Transaction ID zvichioneka tiwane kukubatsirai. Iyi message yabva kusystem yedu inoongorora maslips");
                            Console.WriteLine("Failed to extract text from image.");
                            ClearImage(imagePath);
                            return Ok("No text found in the image, customer notified.");
                        }
                    }

                    // Existing logic for non-image messages (text messages)
                    if (messageType == "chat")
                    {
                        // Process keyword-based responses for external messages
                        var response = await _orderService.HandleKeywordBasedResponses(sender, text);
                        if (response != null)
                        {
                            if (sender == ServiceSenderId)
                            {
                                Console.WriteLine("Ignored outgoing message");
                                return Ok();
                            }
                            Console.WriteLine("Keyword-based response sent.");
                            return Ok("Keyword-based response sent.");
                        }

                        // Check if the message is an order-related message with "ID-"
                        if (text.StartsWith("ID-", StringComparison.OrdinalIgnoreCase))
                        {
                            if (To.Contains("@g.us") && sender == ServiceSenderId)
                            {
                                Console.WriteLine("Ignored outgoing message to group.");
                                return Ok();
                            }
                            if (To.Contains("777202850") || To.Contains("713978760") || To.Contains("772727946") || To.Contains("779052292"))
                            {
                                Console.WriteLine("Ignored outgoing message");
                                return Ok();
                            }
                        }

                        if (sender == ServiceSenderId)
                        {
                            var result = await _orderService.VerifyAndProcessOrder(text, sender, body);
                            return Ok(); // Return appropriate result
                        }
                    }

                    // If no criteria matched, return a default response
                    return Ok("No action taken for the received message.");
                }
            }
            catch (JsonException jsonEx)
            {
                Console.WriteLine($"JSON deserialization error: {jsonEx.Message}");
                return BadRequest("Malformed JSON received.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception occurred: {ex.Message}");
                return StatusCode(500, "An error occurred while processing the request.");
            }
        }

        private async Task<string> DownloadImage(string imageUrl)
        {
            string directoryPath = "./images";
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            string localFilePath = $"{directoryPath}/{Guid.NewGuid()}.jpg";

            try
            {
                var client = _httpClientFactory.CreateClient();
                var response = await client.GetAsync(imageUrl);

                if (response.IsSuccessStatusCode)
                {
                    var imageBytes = await response.Content.ReadAsByteArrayAsync();
                    await System.IO.File.WriteAllBytesAsync(localFilePath, imageBytes);
                    Console.WriteLine("Image downloaded successfully.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error downloading image: {ex.Message}");
            }

            return localFilePath;
        }

        private string ExtractTextFromImage(string imagePath)
        {
            try
            {
                string tessdataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");

                using var engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default);
                using var img = Pix.LoadFromFile(imagePath);
                using var page = engine.Process(img);
                var text = page.GetText();
                return text;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error extracting text from image: {ex.Message}");
                return null;
            }
        }

        private List<string> ExtractTransactionCodes(string text)
        {
            var transactionCodes = new List<string>();

            if (text.Contains("TRACE:"))
            {
                var traceIndex = text.IndexOf("TRACE:") + 6;
                var traceCode = text.Substring(traceIndex).Split('\n')[0].Trim();
                transactionCodes.Add(traceCode);
            }

            if (text.Contains("Transaction ID"))
            {
                var transIndex = text.IndexOf("Transaction ID") + 14;
                var transCode = text.Substring(transIndex).Split('\n')[0].Trim();
                transactionCodes.Add(transCode);
            }

            return transactionCodes.Distinct().ToList();
        }

        private bool IsTransactionCodeDuplicate(string code)
        {
            return _context.TransactionCodes.Any(t => t.Code == code);
        }

        private bool IsTransactionCodeDuplicateForSender(string code, string sender)
        {
            var existingTransaction = _context.TransactionCodes.FirstOrDefault(t => t.Code == code && t.PhoneNumber == sender);
            if (existingTransaction != null)
            {
                // Check if the same sender is trying to submit the same transaction code within 24 hours
                return (DateTime.Now - existingTransaction.Timestamp).TotalHours <= 24;
            }
            return false;
        }

        private void SaveTransactionCode(string code, string phoneNumber)
        {
            var transaction = new TransactionCode
            {
                Code = code,
                PhoneNumber = phoneNumber,
                Timestamp = DateTime.Now
            };
            _context.TransactionCodes.Add(transaction);
            _context.SaveChanges();
        }

        private void ClearImage(string imagePath)
        {
            try
            {
                if (System.IO.File.Exists(imagePath))
                {
                    System.IO.File.Delete(imagePath);
                    Console.WriteLine("Image cleared from storage.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error clearing image: {ex.Message}");
            }
        }
    }
}

//using Microsoft.AspNetCore.Mvc;
//using Newtonsoft.Json;
//using OrderVerificationAPI.Interfaces;
//using OrderVerificationAPI.Models;
//using OrderVerificationAPI.Models.OrderVerificationAPI.Models;
//using System.Collections.Concurrent;
//using System.Net.Http;
//using System.Transactions;
//using Tesseract;

//namespace OrderVerificationAPI.Controllers
//{
//    [ApiController]
//    [Route("api/[controller]")]
//    public class WebhookController : ControllerBase
//    {
//        private readonly IOrderService _orderService;
//        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);
//        private static readonly string ServiceSenderId = "27746262742@c.us";
//        private static readonly ConcurrentDictionary<string, DateTime> ProcessedMessageIds = new ConcurrentDictionary<string, DateTime>();
//        private readonly IHttpClientFactory _httpClientFactory;
//        private readonly AppDbContext _context;

//        public WebhookController(IOrderService orderService, IHttpClientFactory httpClientFactory, AppDbContext context)
//        {
//            _orderService = orderService;
//            _httpClientFactory = httpClientFactory;
//            _context = context;
//        }

//        [HttpPost]
//        public async Task<IActionResult> ReceiveMessage()
//        {
//            try
//            {
//                using (var reader = new StreamReader(Request.Body))
//                {
//                    var body = await reader.ReadToEndAsync();
//                    Console.WriteLine($"Received JSON: {body}");

//                    // Deserialize JSON into UltraMsgWebhook object
//                    var webhook = JsonConvert.DeserializeObject<UltraMsgWebhook>(body);
//                    if (webhook == null || webhook.Data == null)
//                    {
//                        Console.WriteLine("Invalid webhook data received");
//                        return BadRequest("Invalid webhook format.");
//                    }

//                    // Extract message details and a unique message ID
//                    string text = webhook.Data.Body;
//                    string sender = webhook.Data.From;
//                    string messageId = webhook.Data.Id;
//                    string messageType = webhook.Data.Type;
//                    string mediaUrl = webhook.Data.Media;
//                    string To = webhook.Data.To;
//                    var messageTimestamp = DateTimeOffset.FromUnixTimeSeconds(webhook.Data.Time).UtcDateTime;

//                    Console.WriteLine($"Parsed message: Text={text}, Sender={sender}, MessageId={messageId}, Timestamp={messageTimestamp}");

//                    // Check if the message is within a recent time window (e.g., 5 minutes)
//                    if (webhook.Data?.Time != null)
//                    {
//                        DateTime messageDateTime = _orderService.ConvertUnixTimestampToDateTime(webhook.Data.Time);

//                        if (!_orderService.IsMessageRecent(messageDateTime))
//                        {
//                            Console.WriteLine("Ignoring old message.");
//                            return Ok("Old message ignored.");
//                        }
//                    }
//                    else
//                    {
//                        Console.WriteLine("Time field missing or invalid in webhook data.");
//                        return BadRequest("Invalid message time.");
//                    }

//                    // Proceed with existing checks and logic for valid messages
//                    if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(messageId))
//                    {
//                        if (!(messageType == "image"))
//                        {
//                            Console.WriteLine("Message content or ID is missing or invalid");
//                            return BadRequest("Invalid message content.");
//                        }
//                    }

//                    // Ignore messages from the service sender if they are not order-related
//                    if (sender == ServiceSenderId && !text.StartsWith("ID-", StringComparison.OrdinalIgnoreCase))
//                    {
//                        Console.WriteLine("Ignoring non-order message from service sender.");
//                        return Ok("Non-order message from service sender ignored.");
//                    }

//                    // Check if the message has been processed within the cache duration
//                    if (ProcessedMessageIds.ContainsKey(messageId) && (DateTime.Now - ProcessedMessageIds[messageId]) < CacheDuration)
//                    {
//                        Console.WriteLine("Message already processed, skipping.");
//                        return Ok("Duplicate message ignored.");
//                    }

//                    // Add message ID to the processed cache
//                    ProcessedMessageIds[messageId] = DateTime.Now;

//                    // Handle image message
//                    if (messageType == "image" && !string.IsNullOrEmpty(mediaUrl))
//                    {
//                        if(sender.Contains(ServiceSenderId))
//                        {
//                            Console.WriteLine("Ignored outgoing image from service.");
//                            return Ok("Outgoing service backup");
//                        }
//                        var imagePath = await DownloadImage(mediaUrl);

//                        // Extract text from the image
//                        var extractedText = ExtractTextFromImage(imagePath);
//                        if (!string.IsNullOrWhiteSpace(extractedText))
//                        {
//                            var transactionCodes = ExtractTransactionCodes(extractedText);

//                            // Handle cases with multiple transaction codes
//                            if (transactionCodes.Count > 1)
//                            {
//                                await _orderService.SendMessage(sender, $"AI yedu irikuti matumira maslip anopfuura 1 papicture imwe chete. Tumirai 1 slip papicture imwe");
//                                Console.WriteLine("Multiple transaction codes detected, possible multiple slips.");
//                                return Ok("Multiple transaction codes detected. Please send separate images for each slip.");
//                            }
//                            else if (transactionCodes.Count == 1)
//                            {
//                                var transactionCode = transactionCodes.First();

//                                // Check if the transaction code is unique
//                                if (!IsTransactionCodeDuplicate(transactionCode))
//                                {
//                                    // Save transaction code along with sender info
//                                    SaveTransactionCode(transactionCode, sender);
//                                    ClearImage(imagePath);
//                                    return Ok("Transaction code saved.");
//                                }
//                                else
//                                {
//                                    await _orderService.SendMessage(sender, $"System yedu irikuti picture iyi yambotumirwa, musatumira picture imwe chete kaviri asi kana ari agent wedu ati sendai zvakare no problem");
//                                    Console.WriteLine("Duplicate transaction code detected.");
//                                    ClearImage(imagePath);
//                                    return Ok("Duplicate transaction code detected.");
//                                }
//                            }
//                        }
//                        ClearImage(imagePath);
//                    }

//                    // Existing logic for non-image messages (text messages)
//                    if (messageType == "chat")
//                    {
//                        // Process keyword-based responses for external messages
//                        var response = await _orderService.HandleKeywordBasedResponses(sender, text);
//                        if (response != null)
//                        {
//                            if (sender == ServiceSenderId)
//                            {
//                                Console.WriteLine("Ignored outgoing message");
//                                return Ok();
//                            }
//                            Console.WriteLine("Keyword-based response sent.");
//                            return Ok("Keyword-based response sent.");
//                        }

//                        // Check if the message is an order-related message with "ID-"
//                        if (text.StartsWith("ID-", StringComparison.OrdinalIgnoreCase))
//                        {
//                            if (To.Contains("777202850") || To.Contains("713978760") || To.Contains("772727946") || To.Contains("779052292"))
//                            {
//                                Console.WriteLine("Ignored outgoing message");
//                                return Ok();
//                            }
//                        }

//                        if (sender == ServiceSenderId)
//                        {
//                            var result = await _orderService.VerifyAndProcessOrder(text, sender, body);
//                            return Ok(); // Return appropriate result
//                        }
//                    }

//                    // If no criteria matched, return a default response
//                    return Ok("No action taken for the received message.");
//                }
//            }
//            catch (JsonException jsonEx)
//            {
//                Console.WriteLine($"JSON deserialization error: {jsonEx.Message}");
//                return BadRequest("Malformed JSON received.");
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Exception occurred: {ex.Message}");
//                return StatusCode(500, "An error occurred while processing the request.");
//            }
//        }

//        private async Task<string> DownloadImage(string imageUrl)
//        {
//            string directoryPath = "./images";
//            if (!Directory.Exists(directoryPath))
//            {
//                Directory.CreateDirectory(directoryPath);
//            }

//            string localFilePath = $"{directoryPath}/{Guid.NewGuid()}.jpg";

//            try
//            {
//                var client = _httpClientFactory.CreateClient();
//                var response = await client.GetAsync(imageUrl);

//                if (response.IsSuccessStatusCode)
//                {
//                    var imageBytes = await response.Content.ReadAsByteArrayAsync();
//                    await System.IO.File.WriteAllBytesAsync(localFilePath, imageBytes);
//                    Console.WriteLine("Image downloaded successfully.");
//                }
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Error downloading image: {ex.Message}");
//            }

//            return localFilePath;
//        }

//        private string ExtractTextFromImage(string imagePath)
//        {
//            try
//            {
//                string tessdataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");

//                using var engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default);
//                using var img = Pix.LoadFromFile(imagePath);
//                using var page = engine.Process(img);
//                var text = page.GetText();
//                return text;
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Error extracting text from image: {ex.Message}");
//                return null;
//            }
//        }

//        private List<string> ExtractTransactionCodes(string text)
//        {
//            var transactionCodes = new List<string>();

//            if (text.Contains("TRACE:"))
//            {
//                var traceIndex = text.IndexOf("TRACE:") + 6;
//                var traceCode = text.Substring(traceIndex).Split('\n')[0].Trim();
//                transactionCodes.Add(traceCode);
//            }

//            if (text.Contains("Transaction ID"))
//            {
//                var transIndex = text.IndexOf("Transaction ID") + 14;
//                var transCode = text.Substring(transIndex).Split('\n')[0].Trim();
//                transactionCodes.Add(transCode);
//            }

//            return transactionCodes.Distinct().ToList();
//        }

//        private bool IsTransactionCodeDuplicate(string code)
//        {
//            return _context.TransactionCodes.Any(t => t.Code == code);
//        }

//        private void SaveTransactionCode(string code, string phoneNumber)
//        {
//            var transaction = new TransactionCode
//            {
//                Code = code,
//                PhoneNumber = phoneNumber,
//                Timestamp = DateTime.Now
//            };
//            _context.TransactionCodes.Add(transaction);
//            _context.SaveChanges();
//        }

//        private void ClearImage(string imagePath)
//        {
//            try
//            {
//                if (System.IO.File.Exists(imagePath))
//                {
//                    System.IO.File.Delete(imagePath);
//                    Console.WriteLine("Image cleared from storage.");
//                }
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Error clearing image: {ex.Message}");
//            }
//        }
//    }
//}






//using Microsoft.AspNetCore.Mvc;
//using Newtonsoft.Json;
//using OrderVerificationAPI.Interfaces;
//using OrderVerificationAPI.Models;
//using OrderVerificationAPI.Models.OrderVerificationAPI.Models;
//using System.Collections.Concurrent;
//using System.Net.Http;
//using System.Transactions;
//using Tesseract;

//namespace OrderVerificationAPI.Controllers
//{
//    [ApiController]
//    [Route("api/[controller]")]
//    public class WebhookController : ControllerBase
//    {
//        private readonly IOrderService _orderService;
//        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);
//        private static readonly string ServiceSenderId = "27746262742@c.us"; //27746262742
//        private static readonly ConcurrentDictionary<string, DateTime> ProcessedMessageIds = new ConcurrentDictionary<string, DateTime>();
//        private readonly IHttpClientFactory _httpClientFactory;
//        public WebhookController(IOrderService orderService, IHttpClientFactory httpClientFactory)
//        {
//            _orderService = orderService;
//            _httpClientFactory = httpClientFactory;
//        }

//        [HttpPost]
//        [HttpPost]
//        public async Task<IActionResult> ReceiveMessage()
//        {
//            try
//            {
//                using (var reader = new StreamReader(Request.Body))
//                {
//                    var body = await reader.ReadToEndAsync();
//                    Console.WriteLine($"Received JSON: {body}");

//                    // Deserialize JSON into UltraMsgWebhook object
//                    var webhook = JsonConvert.DeserializeObject<UltraMsgWebhook>(body);
//                    if (webhook == null || webhook.Data == null)
//                    {
//                        Console.WriteLine("Invalid webhook data received");
//                        return BadRequest("Invalid webhook format.");
//                    }

//                    // Extract message details and a unique message ID
//                    string text = webhook.Data.Body;
//                    string sender = webhook.Data.From;
//                    string messageId = webhook.Data.Id;
//                    string messageType = webhook.Data.Type;
//                    string mediaUrl = webhook.Data.Media;
//                    string To = webhook.Data.To;
//                    var messageTimestamp = DateTimeOffset.FromUnixTimeSeconds(webhook.Data.Time).UtcDateTime; var currentTime = DateTime.UtcNow;
//                    DateTime now = DateTime.UtcNow;
//                    Console.WriteLine($"Parsed message: Text={text}, Sender={sender}, MessageId={messageId}, Timestamp={messageTimestamp}");

//                    // Convert timestamp to DateTime
//                    //DateTime messageTime = DateTimeOffset.FromUnixTimeSeconds(messageTimestamp).DateTime;
//                    //DateTime currentTime = DateTime.UtcNow;

//                    // Check if the message is within a recent time window (e.g., 5 minutes)
//                    if (webhook.Data?.Time != null)
//                    {
//                        // Convert the time field to DateTime
//                        DateTime messageDateTime = _orderService.ConvertUnixTimestampToDateTime(webhook.Data.Time);

//                        // Check if the message is recent (within the last 5 minutes)
//                        if (!_orderService.IsMessageRecent(messageDateTime))
//                        {
//                            Console.WriteLine("Ignoring old message.");
//                            return Ok("Old message ignored.");
//                        }
//                    }
//                    else
//                    {
//                        Console.WriteLine("Time field missing or invalid in webhook data.");
//                        return BadRequest("Invalid message time.");
//                    }

//                    // Proceed with the existing checks and logic for valid messages
//                    if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(messageId))
//                    {
//                        Console.WriteLine("Message content or ID is missing or invalid");
//                        return BadRequest("Invalid message content.");
//                    }

//                    // Ignore messages from the service sender if they are not order-related
//                    if (sender == ServiceSenderId && !text.StartsWith("ID-", StringComparison.OrdinalIgnoreCase))
//                    {
//                        Console.WriteLine("Ignoring non-order message from service sender.");
//                        return Ok("Non-order message from service sender ignored.");
//                    }

//                    // Check if the message has been processed within the cache duration
//                    if (ProcessedMessageIds.ContainsKey(messageId) && (DateTime.Now - ProcessedMessageIds[messageId]) < CacheDuration)
//                    {
//                        Console.WriteLine("Message already processed, skipping.");
//                        return Ok("Duplicate message ignored.");
//                    }

//                    // Add message ID to the processed cache
//                    ProcessedMessageIds[messageId] = DateTime.Now;

//                    if (messageType != "chat")
//                    {
//                        Console.WriteLine("Non-text message received. Skipping auto-response.");
//                        return Ok("Non-text message skipped.");
//                    }

//                    // Process keyword-based responses for external messages
//                    var response = await _orderService.HandleKeywordBasedResponses(sender, text);
//                    if (response != null)
//                    {
//                        if (sender == ServiceSenderId)
//                        {
//                            Console.WriteLine("Ignored outgoing message");
//                            return Ok();
//                        }
//                        Console.WriteLine("Keyword-based response sent.");
//                        return Ok("Keyword-based response sent.");
//                    }

//                    // Check if the message is an order-related message with "ID-"
//                    if (text.StartsWith("ID-", StringComparison.OrdinalIgnoreCase))
//                    {
//                        if (To.ToString().Contains("777202850"))
//                        {
//                            Console.WriteLine("Ignored outgoing message");
//                            return Ok();
//                        }
//                        if (To.ToString().Contains("713978760"))
//                            Console.WriteLine("Ignored outgoing message");
//                        return Ok();
//                    }
//                    if (To.ToString().Contains("772727946"))
//                    {
//                        Console.WriteLine("Ignored outgoing message");
//                        return Ok();
//                    }
//                    if (To.ToString().Contains("779052292"))
//                    {
//                        Console.WriteLine("Ignored outgoing message");
//                        return Ok();
//                    }
//                    if (sender == ServiceSenderId)
//                    {
//                        var result = await _orderService.VerifyAndProcessOrder(text, sender, body);
//                        return Ok(); //result);
//                    }


//                    // If no criteria matched, return a default response
//                    return Ok("No action taken for the received message.");
//                }
//            }
//            catch (JsonException jsonEx)
//            {
//                Console.WriteLine($"JSON deserialization error: {jsonEx.Message}");
//                return BadRequest("Malformed JSON received.");
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Exception occurred: {ex.Message}");
//                return StatusCode(500, "An error occurred while processing the request.");
//            }
//        }

//        private async Task<string> DownloadImage(string imageUrl)
//        {
//            string directoryPath = "./images";
//            if (!Directory.Exists(directoryPath))
//            {
//                Directory.CreateDirectory(directoryPath);
//            }

//            string localFilePath = $"{directoryPath}/{Guid.NewGuid()}.jpg";

//            try
//            {
//                var client = _httpClientFactory.CreateClient();
//                var response = await client.GetAsync(imageUrl);

//                if (response.IsSuccessStatusCode)
//                {
//                    var imageBytes = await response.Content.ReadAsByteArrayAsync();
//                    await System.IO.File.WriteAllBytesAsync(localFilePath, imageBytes);
//                    Console.WriteLine("Image downloaded successfully.");
//                }
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Error downloading image: {ex.Message}");
//            }

//            return localFilePath;
//        }

//        private string ExtractTextFromImage(string imagePath)
//        {
//            try
//            {
//                // Use a relative path based on the application's base directory
//                string tessdataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");

//                using var engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default);
//                using var img = Pix.LoadFromFile(imagePath);
//                using var page = engine.Process(img);
//                var text = page.GetText();
//                return text;
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Error extracting text from image: {ex.Message}");
//                return null;
//            }
//        }


//        private void ClearImage(string imagePath)
//        {
//            try
//            {
//                if (System.IO.File.Exists(imagePath))
//                {
//                    System.IO.File.Delete(imagePath);
//                    Console.WriteLine("Image cleared from storage.");
//                }
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Error clearing image: {ex.Message}");
//            }
//        }
//    }
//}





//using Microsoft.AspNetCore.Mvc;
//using Newtonsoft.Json;
//using OrderVerificationAPI.Interfaces;
//using OrderVerificationAPI.Models;
//using System.Collections.Concurrent;
//using System.Net.Http;
//using Tesseract;

//namespace OrderVerificationAPI.Controllers
//{
//    [ApiController]
//    [Route("api/[controller]")]
//    public class WebhookController : ControllerBase
//    {
//        private readonly IOrderService _orderService;
//        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);
//        private static readonly ConcurrentDictionary<string, DateTime> ProcessedMessageIds = new ConcurrentDictionary<string, DateTime>();
//        private readonly IHttpClientFactory _httpClientFactory;
//        private readonly AppDbContext _context; // Assuming you have a DbContext to handle database operations

//        public WebhookController(IOrderService orderService, IHttpClientFactory httpClientFactory, AppDbContext context)
//        {
//            _orderService = orderService;
//            _httpClientFactory = httpClientFactory;
//            _context = context;
//        }

//        [HttpPost]
//        public async Task<IActionResult> ReceiveMessage()
//        {
//            try
//            {
//                using (var reader = new StreamReader(Request.Body))
//                {
//                    var body = await reader.ReadToEndAsync();
//                    var webhook = JsonConvert.DeserializeObject<UltraMsgWebhook>(body);

//                    if (webhook?.Data == null)
//                        return BadRequest("Invalid webhook format.");

//                    string mediaUrl = webhook.Data.Media;
//                    string messageType = webhook.Data.Type;

//                    if (messageType == "image" && !string.IsNullOrEmpty(mediaUrl))
//                    {
//                        var imagePath = await DownloadImage(mediaUrl);
//                        var extractedText = ExtractTextFromImage(imagePath);

//                        if (extractedText != null)
//                        {
//                            var transactionCode = ExtractTransactionCode(extractedText);

//                            if (!string.IsNullOrEmpty(transactionCode))
//                            {
//                                if (!IsTransactionCodeDuplicate(transactionCode))
//                                {
//                                    SaveTransactionCode(transactionCode);
//                                    ClearImage(imagePath);
//                                    return Ok("Transaction code saved.");
//                                }
//                                return Ok("Duplicate transaction code detected.");
//                            }
//                        }
//                        ClearImage(imagePath);
//                    }

//                    return Ok("No action taken for the received message.");
//                }
//            }
//            catch (Exception ex)
//            {
//                return StatusCode(500, $"An error occurred: {ex.Message}");
//            }
//        }

//        private async Task<string> DownloadImage(string imageUrl)
//        {
//            var client = _httpClientFactory.CreateClient();
//            string localFilePath = $"./images/{Guid.NewGuid()}.jpg";

//            var response = await client.GetAsync(imageUrl);
//            if (response.IsSuccessStatusCode)
//            {
//                var imageBytes = await response.Content.ReadAsByteArrayAsync();
//                await System.IO.File.WriteAllBytesAsync(localFilePath, imageBytes);
//            }

//            return localFilePath;
//        }

//        private string ExtractTextFromImage(string imagePath)
//        {
//            try
//            {
//                using var engine = new TesseractEngine("./tessdata", "eng", EngineMode.Default);
//                using var img = Pix.LoadFromFile(imagePath);
//                using var page = engine.Process(img);
//                return page.GetText();
//            }
//            catch
//            {
//                return null;
//            }
//        }

//        private string ExtractTransactionCode(string text)
//        {
//            if (text.Contains("TRACE:"))
//            {
//                var traceIndex = text.IndexOf("TRACE:") + 6;
//                return text.Substring(traceIndex).Split('\n')[0].Trim();
//            }
//            if (text.Contains("Transaction ID"))
//            {
//                var transIndex = text.IndexOf("Transaction ID") + 14;
//                return text.Substring(transIndex).Split('\n')[0].Trim();
//            }
//            return null;
//        }

//        private bool IsTransactionCodeDuplicate(string code)
//        {
//            return _context.TransactionCodes.Any(t => t.Code == code);
//        }

//        private void SaveTransactionCode(string code)
//        {
//            var transaction = new TransactionCode { Code = code, Timestamp = DateTime.Now };
//            _context.TransactionCodes.Add(transaction);
//            _context.SaveChanges();
//        }

//        private void ClearImage(string imagePath)
//        {
//            if (System.IO.File.Exists(imagePath))
//                System.IO.File.Delete(imagePath);
//        }
//    }
//}
