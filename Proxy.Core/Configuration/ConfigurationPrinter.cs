using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Orbyn.Proxy.Core.Configuration;

internal static class ConfigurationPrinter
{
    public static void WriteRedacted(ProxyOptions options, bool serviceMode)
    {
        var serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        serializerOptions.Converters.Add(new JsonStringEnumConverter());
        var root = JsonSerializer.SerializeToNode(options, serializerOptions);

        RedactSecrets(root);
        if (root is JsonObject rootObject)
        {
            rootObject.Insert(0, "Execution", new JsonObject
            {
                ["Mode"] = serviceMode ? "Service" : "Terminal"
            });
        }

        Console.WriteLine(root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RedactSecrets(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (property.Key.Contains("Password", StringComparison.OrdinalIgnoreCase) &&
                    !property.Key.Contains("EnvironmentVariable", StringComparison.OrdinalIgnoreCase))
                {
                    jsonObject[property.Key] = string.IsNullOrEmpty(property.Value?.GetValue<string>())
                        ? string.Empty
                        : "***REDACTED***";
                    continue;
                }

                RedactSecrets(property.Value);
            }

            return;
        }

        if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                RedactSecrets(item);
            }
        }
    }
}
