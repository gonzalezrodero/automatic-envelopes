using Amazon.BedrockRuntime.Model;
using AutomaticEnvelopes.Api.Features.Chat.Tools;
using System.Text.Json;
using System.Text.Json.Serialization;
using JsonConverter = AutomaticEnvelopes.Api.Features.Tenants.Helpers.JsonConverter;

namespace AutomaticEnvelopes.Api.Features.Tenants.Sama.CampusPricing;

[JsonSerializable(typeof(CampusFamilyArguments))]
[JsonSerializable(typeof(CampusFamilyResult))]
public partial class CampusToolJsonContext : JsonSerializerContext
{
}

public class CampusCalculatorTool(ILogger<CampusCalculatorTool> logger) : IBedrockTool
{
    public const string ToolName = "calculate_family_campus_price";

    public string Name => ToolName;

    public ToolSpecification GetSpecification()
    {
        var schemaJson = """
        {
            "type": "object",
            "properties": {
                "campusType": {
                    "type": "string",
                    "enum": ["September"],
                    "description": "The type of campus being priced. ALWAYS set this to 'September'."
                },
                "isSocio": {
                    "type": "boolean",
                    "description": "True if ANY family member is a club member or from Escola Ginesta."
                },
                "familyDiscountType": {
                    "type": "string",
                    "enum": ["None", "Germa", "Familia Nombrosa", "Familia Monoparental"],
                    "description": "The family discount requested by the user. Pass exactly what they ask for (e.g., 'Germa' if they mention siblings)."
                },
                "isAfterMay1st": {
                    "type": "boolean",
                    "description": "True if the current system date is strictly after May 1st of the current year. (Summer only)"
                },
                "participants": {
                    "type": "array",
                    "description": "List of children being enrolled.",
                    "items": {
                        "type": "object",
                        "properties": {
                            "name": { "type": "string" },
                            "campusWeeks": { "type": "integer", "description": "Number of regular Summer Campus weeks (0 to 6)." },
                            "septemberDays": { "type": "integer", "description": "Number of days for the September Campus (0 to 10)." },
                            "tecnificacioWeeks": { "type": "integer", "description": "Number of Tecnificacio program weeks (0 to 4)." },
                            "menjadorDays": { "type": "integer", "description": "Number of days using the Menjador service." },
                            "tardaDays": { "type": "integer", "description": "Number of days using the Tarda service." },
                            "excursionsCost": { "type": "integer", "description": "The total raw cost of any requested excursions." }
                        },
                        "required": ["name", "campusWeeks", "septemberDays", "tecnificacioWeeks", "menjadorDays", "tardaDays", "excursionsCost"]
                    }
                }
            },
            "required": ["campusType", "isSocio", "familyDiscountType", "isAfterMay1st", "participants"]
        }
        """;

        using var jsonDoc = JsonDocument.Parse(schemaJson);
        return new ToolSpecification
        {
            Name = ToolName,
            Description = "Calculates the total campus price (Summer or September) for an entire family.",
            InputSchema = new ToolInputSchema
            {
                Json = JsonConverter.ToAwsDocument(jsonDoc.RootElement)
            }
        };
    }

    public Task<string> ExecuteAsync(string jsonArguments, CancellationToken ct)
    {
        logger.LogInformation("Executing CampusCalculatorTool logic.");
        var args = JsonSerializer.Deserialize(jsonArguments, CampusToolJsonContext.Default.CampusFamilyArguments);

        if (args?.Participants == null || args.Participants.Count == 0)
        {
            logger.LogWarning("Invalid or empty participants array provided by Bedrock.");
            return Task.FromResult("Error: Invalid arguments.");
        }

        if (args.CampusType == "September" && args.Participants.Any(p => p.SeptemberDays > 10))
        {
            logger.LogWarning("September campus maximum days exceeded.");
            return Task.FromResult("Avís: El campus de setembre només té un màxim de 10 dies disponibles (del 24 d'agost al 4 de setembre). Si us plau, informa l'usuari d'aquest límit.");
        }

        var siblingWithLeastWeeks = args.CampusType == "Summer" ? SummerCampusCalculator.GetSiblingWithLeastWeeks(args) : null;
        var participantResults = new List<ParticipantPricingResult>();
        decimal familyGrandTotal = 0;

        foreach (var p in args.Participants)
        {
            var result = args.CampusType == "September"
                ? SeptemberCampusCalculator.Calculate(p, args)
                : SummerCampusCalculator.Calculate(p, args, siblingWithLeastWeeks);

            participantResults.Add(result);
            familyGrandTotal += result.Total;
        }

        var finalResult = new CampusFamilyResult(familyGrandTotal, participantResults);
        return Task.FromResult(JsonSerializer.Serialize(finalResult, CampusToolJsonContext.Default.CampusFamilyResult));
    }
}