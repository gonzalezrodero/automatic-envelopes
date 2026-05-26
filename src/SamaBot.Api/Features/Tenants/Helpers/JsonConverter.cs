using Amazon.Runtime.Documents;
using System.Text;
using System.Text.Json;

namespace SamaBot.Api.Features.Tenants.Helpers;

public static class JsonConverter
{
    // --- FROM JsonElement TO AWS Document (Used in Schema Generation) ---
    public static Document ToAwsDocument(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => new Document(element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ToAwsDocument(p.Value))),

            JsonValueKind.Array => new Document(element.EnumerateArray()
                .Select(ToAwsDocument).ToList()),

            JsonValueKind.String => new Document(element.GetString()),

            JsonValueKind.Number => ParseNumber(element),

            JsonValueKind.True => new Document(true),
            JsonValueKind.False => new Document(false),
            _ => new Document() // Handles Nulls
        };
    }

    private static Document ParseNumber(JsonElement element)
    {
        if (element.TryGetInt32(out var i)) return new Document(i);
        if (element.TryGetDouble(out var d)) return new Document(d);

        return new Document(element.GetRawText());
    }

    // --- FROM AWS Document TO JSON String (Used in Tool Execution) ---
    public static string ToJsonString(Document doc)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        WriteDocument(writer, doc);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteDocument(Utf8JsonWriter writer, Document doc)
    {
        if (doc.IsNull())
        {
            writer.WriteNullValue();
        }
        else if (doc.IsBool())
        {
            writer.WriteBooleanValue(doc.AsBool());
        }
        else if (doc.IsString())
        {
            writer.WriteStringValue(doc.AsString());
        }
        else if (doc.IsInt())
        {
            writer.WriteNumberValue(doc.AsInt());
        }
        else if (doc.IsDouble())
        {
            writer.WriteNumberValue(doc.AsDouble());
        }
        else if (doc.IsList())
        {
            writer.WriteStartArray();
            foreach (var item in doc.AsList())
            {
                WriteDocument(writer, item);
            }
            writer.WriteEndArray();
        }
        else if (doc.IsDictionary())
        {
            writer.WriteStartObject();
            foreach (var kvp in doc.AsDictionary())
            {
                writer.WritePropertyName(kvp.Key);
                WriteDocument(writer, kvp.Value);
            }
            writer.WriteEndObject();
        }
        else
        {
            // Fallback for edge cases like Long or unmapped numeric types
            writer.WriteStringValue(doc.ToString());
        }
    }
}