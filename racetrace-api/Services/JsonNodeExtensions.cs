using System.Globalization;
using System.Text.Json.Nodes;

namespace RaceTrace.Api.Services;

public static class JsonNodeExtensions
{
    public static string? String(this JsonObject json, string name)
    {
        if (!json.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : node.ToString();
    }

    public static int? Int(this JsonObject json, string name)
    {
        if (!json.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node.GetValueKind() == System.Text.Json.JsonValueKind.Number && node.AsValue().TryGetValue<int>(out var number))
        {
            return number;
        }

        if (int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return decimal.TryParse(node.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue)
            ? (int?)decimal.ToInt32(decimalValue)
            : null;
    }

    public static decimal? Decimal(this JsonObject json, string name)
    {
        if (!json.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node.GetValueKind() == System.Text.Json.JsonValueKind.Number && node.AsValue().TryGetValue<decimal>(out var number))
        {
            return number;
        }

        return decimal.TryParse(node.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    public static bool? Bool(this JsonObject json, string name)
    {
        if (!json.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
        {
            return node.GetValue<bool>();
        }

        var text = node.ToString();
        if (bool.TryParse(text, out var parsed))
        {
            return parsed;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number != 0;
        }

        return null;
    }

    public static JsonObject? Object(this JsonObject json, string name)
    {
        return json.TryGetPropertyValue(name, out var node) ? node as JsonObject : null;
    }

    public static JsonArray? Array(this JsonObject json, string name)
    {
        return json.TryGetPropertyValue(name, out var node) ? node as JsonArray : null;
    }

    public static string DisplayName(this JsonObject rider)
    {
        var firstName = rider.String("first_name");
        var lastName = rider.String("last_name");
        var displayName = string.Join(" ", new[] { firstName, lastName }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(displayName) ? rider.String("uci_id") ?? rider.String("id") ?? string.Empty : displayName;
    }
}
