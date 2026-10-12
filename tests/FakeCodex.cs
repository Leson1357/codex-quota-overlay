using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

// Test-only stdio reader: never connects to an account or network.
public static class FakeCodex
{
    public static void Main()
    {
        var json = new JavaScriptSerializer();
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            var message = json.Deserialize<Dictionary<string, object>>(line);
            if (!message.ContainsKey("id")) continue;
            object id = message["id"];
            string method = Convert.ToString(message["method"]);
            if (method == "initialize")
            {
                Console.WriteLine("{\"id\":\"ignored-server-request\",\"method\":\"test/notification\"}");
                Console.WriteLine(json.Serialize(new { id, result = new { userAgent = "test" } }));
            }
            else if (method == "account/rateLimits/read")
            {
                var mode = Environment.GetEnvironmentVariable("CODEX_QUOTA_TEST_MODE");
                if (mode == "timeout") continue;
                if (mode == "error")
                {
                    Console.WriteLine(json.Serialize(new { id, error = new { code = -32603, message = "Test failure" } }));
                    continue;
                }
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Console.WriteLine(json.Serialize(new
                {
                    id,
                    result = new
                    {
                        rateLimits = new
                        {
                            // Deliberately reversed: duration must determine which label is used.
                            primary = new { usedPercent = 25, windowDurationMins = 10080, resetsAt = now + 172801 },
                            secondary = new { usedPercent = 7, windowDurationMins = 300, resetsAt = now + 3723 }
                        }
                    }
                }));
            }
        }
    }
}
