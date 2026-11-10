using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RaceTrace.Api.Infrastructure;

// HTTP only: database/provider JSON keeps its original keys in memory and on input.
internal sealed class ApiJsonNodeConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeof(JsonNode).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        (JsonConverter)
            Activator.CreateInstance(
                typeof(NodeConverter<>).MakeGenericType(typeToConvert),
                nonPublic: true
            )!;

    private sealed class NodeConverter<TNode> : JsonConverter<TNode>
        where TNode : JsonNode
    {
        public override TNode? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        )
        {
            var node = JsonNode.Parse(ref reader);
            return node is null
                ? null
                : node as TNode ?? throw new JsonException($"Expected {typeToConvert.Name}.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            TNode value,
            JsonSerializerOptions options
        ) => WriteNode(writer, value);
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                writer.WriteStartObject();
                foreach (var property in value)
                {
                    writer.WritePropertyName(CamelCaseField(property.Key));
                    WriteNode(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray value:
                writer.WriteStartArray();
                foreach (var item in value)
                    WriteNode(writer, item);
                writer.WriteEndArray();
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    internal static string CamelCaseField(string name)
    {
        if (!name.Contains('_'))
            return name;
        var result = new StringBuilder(name.Length);
        var capitalize = false;
        foreach (var character in name)
        {
            if (character == '_')
            {
                capitalize = result.Length > 0;
                continue;
            }
            result.Append(capitalize ? char.ToUpperInvariant(character) : character);
            capitalize = false;
        }
        return result.ToString();
    }
}
