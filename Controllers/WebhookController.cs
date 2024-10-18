using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using OrderVerificationAPI.Interfaces;
using OrderVerificationAPI.Models;
using OrderVerificationAPI.Models.OrderVerificationAPI.Models;

namespace OrderVerificationAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WebhookController : ControllerBase
    {
        private readonly IOrderService _orderService;

        public WebhookController(IOrderService orderService)
        {
            _orderService = orderService;
        }
        [HttpPost]
        public async Task<IActionResult> ReceiveMessage()
        {
            try
            {
                // Read the raw request body
                using (var reader = new StreamReader(Request.Body))
                {
                    var body = await reader.ReadToEndAsync();

                    // Log raw JSON for debugging purposes
                    Console.WriteLine($"Received JSON: {body}");

                    // Deserialize JSON into UltraMsgWebhook object
                    var webhook = JsonConvert.DeserializeObject<UltraMsgWebhook>(body);

                    if (webhook == null || webhook.Data == null)
                    {
                        Console.WriteLine("Invalid webhook data received");
                        return BadRequest("Invalid webhook format.");
                    }

                    // Extract message details
                    string text = webhook.Data.Body;
                    string sender = webhook.Data.From;

                    Console.WriteLine($"Parsed message: Text={text}, Sender={sender}");

                    if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(sender))
                    {
                        Console.WriteLine("Message content is missing or invalid");
                        return BadRequest("Invalid message content.");
                    }

                    // Proceed with order verification and processing
                    var result = await _orderService.VerifyAndProcessOrder(text, sender, body);
                    return Ok(result);
                }
            }
            catch (JsonException jsonEx)
            {
                // Handle deserialization issues
                Console.WriteLine($"JSON deserialization error: {jsonEx.Message}");
                return BadRequest("Malformed JSON received.");
            }
            catch (Exception ex)
            {
                // Handle general errors
                Console.WriteLine($"Exception occurred: {ex.Message}");
                return StatusCode(500, "An error occurred while processing the request.");
            }
        }

    }

}
