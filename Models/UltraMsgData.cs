using Newtonsoft.Json;

namespace OrderVerificationAPI.Models
{
    namespace OrderVerificationAPI.Models
    {
        public class UltraMsgWebhook
        {
            [JsonProperty("event_type")]
            public string EventType { get; set; }
            public string InstanceId { get; set; }
            public UltraMsgData Data { get; set; }
        }

        public class UltraMsgData
        {
            public string Id { get; set; }
            public string From { get; set; }
            public string To { get; set; }
            public string Author { get; set; }
            public string Body { get; set; }
            public string Pushname { get; set; }
            public string Ack { get; set; }
            public string Type { get; set; }
            public string Media { get; set; }
            public long Time { get; set; }
        }
    }

}
