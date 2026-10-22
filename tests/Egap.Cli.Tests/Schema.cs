using System.Text.Json;

namespace Egap.Cli.Tests;

/// <summary>
/// Checks JSON against the published schemas in docs/schemas: the parts of JSON Schema they use (type, const, enum,
/// required, properties, additionalProperties: false, items, minimum and $ref into $defs), so a report and its schema can't drift.
/// </summary>
internal static class Schema
{
    /// <summary>Checks <paramref name="json"/> against docs/schemas/<paramref name="name"/> and returns the problems found.</summary>
    public static List<string> Check(string name, string json)
    {
        string folder = Path.Combine(Path.GetDirectoryName(Tool.Files)!, "..", "docs", "schemas");
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, name)));
        using JsonDocument instance = JsonDocument.Parse(json);
        List<string> problems = [];
        Check(schema.RootElement, schema.RootElement, instance.RootElement, "$", problems);
        return problems;
    }

    private static void Check(JsonElement root, JsonElement schema, JsonElement value, string at, List<string> problems)
    {
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            string path = reference.GetString()!;
            schema = root.GetProperty("$defs").GetProperty(path["#/$defs/".Length..]);
        }

        if (schema.TryGetProperty("const", out JsonElement constant) && !JsonElement.DeepEquals(constant, value))
        {
            problems.Add($"{at}: {value} is not {constant}");
        }

        if (schema.TryGetProperty("enum", out JsonElement options) && !options.EnumerateArray().Any(o => JsonElement.DeepEquals(o, value)))
        {
            problems.Add($"{at}: {value} is not one of {options}");
        }

        if (schema.TryGetProperty("type", out JsonElement type))
        {
            string[] types = type.ValueKind == JsonValueKind.Array ? [.. type.EnumerateArray().Select(t => t.GetString()!)] : [type.GetString()!];
            if (!types.Any(t => Is(t, value)))
            {
                problems.Add($"{at}: {value.ValueKind} is not {string.Join(" or ", types)}");
                return;
            }
        }

        if (schema.TryGetProperty("minimum", out JsonElement minimum) && value.ValueKind == JsonValueKind.Number && value.GetDouble() < minimum.GetDouble())
        {
            problems.Add($"{at}: {value} is below {minimum}");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            JsonElement properties = schema.TryGetProperty("properties", out JsonElement p) ? p : default;
            if (schema.TryGetProperty("required", out JsonElement required))
            {
                foreach (JsonElement key in required.EnumerateArray())
                {
                    if (!value.TryGetProperty(key.GetString()!, out _))
                    {
                        problems.Add($"{at}: {key} is missing");
                    }
                }
            }

            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out JsonElement inner))
                {
                    Check(root, inner, property.Value, $"{at}.{property.Name}", problems);
                }
                else if (schema.TryGetProperty("additionalProperties", out JsonElement additional) && additional.ValueKind == JsonValueKind.False)
                {
                    problems.Add($"{at}: {property.Name} is not in the schema");
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items))
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                Check(root, items, item, $"{at}[{index++}]", problems);
            }
        }
    }

    private static bool Is(string type, JsonElement value)
    {
        return type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };
    }
}
