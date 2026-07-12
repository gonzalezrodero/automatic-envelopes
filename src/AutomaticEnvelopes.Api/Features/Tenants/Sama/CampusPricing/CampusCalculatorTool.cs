using Amazon.BedrockRuntime.Model;
using AutomaticEnvelopes.Api.Features.Chat;
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
    public string Tenant => "club-basquet-sama";

    public ToolSpecification GetSpecification()
    {
        var schemaJson = """
        {
            "type": "object",
            "properties": {
                "isSocio": {
                    "type": "boolean",
                    "description": "True if ANY family member is a club member or from Escola Ginesta."
                },
                "familyDiscountType": {
                    "type": "string",
                    "enum": ["None", "Germa", "Familia Nombrosa", "Familia Monoparental"],
                    "description": "The family discount requested by the user. Pass exactly what they ask for (e.g., 'Germa' if they mention siblings), even if you think it doesn't apply. The calculator logic will validate it."
                },
                "isAfterMay1st": {
                    "type": "boolean",
                    "description": "True if the current system date is strictly after May 1st of the current year. This applies a 10% late registration surcharge."
                },
                "participants": {
                    "type": "array",
                    "description": "List of children being enrolled.",
                    "items": {
                        "type": "object",
                        "properties": {
                            "name": { "type": "string" },
                            "campusWeeks": { 
                                "type": "integer",
                                "description": "Number of regular Campus weeks (0 to 6). Default to 0 if not attending." 
                            },
                            "tecnificacioWeeks": { 
                                "type": "integer",
                                "description": "Number of Tecnificacio program weeks (0 to 4). Default to 0 if not attending." 
                            },
                            "menjadorDays": { 
                                "type": "integer",
                                "description": "Number of days using the Menjador service. Default to 0." 
                            },
                            "tardaDays": { 
                                "type": "integer",
                                "description": "Number of days using the Tarda service. Default to 0." 
                            },
                            "excursionsCost": { 
                                "type": "integer", 
                                "description": "The total raw cost of any requested excursions (e.g., 65 for PortAventura). Default to 0." 
                            }
                        },
                        "required": ["name", "campusWeeks", "tecnificacioWeeks", "menjadorDays", "tardaDays", "excursionsCost"]
                    }
                }
            },
            "required": ["isSocio", "familyDiscountType", "isAfterMay1st", "participants"]
        }
        """;

        using var jsonDoc = JsonDocument.Parse(schemaJson);
        return new ToolSpecification
        {
            Name = "calculate_family_campus_price",
            Description = "Calculates the total summer campus price for an entire family, automatically applying sibling and volume discounts, as well as late registration surcharges.",
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

        logger.LogInformation("Calculating pricing for {ParticipantCount} participants. IsSocio: {IsSocio}, DiscountType: {DiscountType}",
            args.Participants.Count, args.IsSocio, args.FamilyDiscountType);

        // Pre-calculate sibling logic to avoid doing it inside the loop
        var siblingWithLeastWeeks = GetSiblingWithLeastWeeks(args);

        var participantResults = new List<ParticipantPricingResult>();
        decimal familyGrandTotal = 0;

        foreach (var p in args.Participants)
        {
            var result = CalculateParticipantPricing(p, args, siblingWithLeastWeeks);

            participantResults.Add(result);
            familyGrandTotal += result.Total;

            logger.LogDebug("Participant '{Name}' calculated. Total: {ParticipantTotal}", p.Name, result.Total);
        }

        logger.LogInformation("Campus pricing calculation completed. Family Grand Total: {FamilyGrandTotal}", familyGrandTotal);

        var finalResult = new CampusFamilyResult(familyGrandTotal, participantResults);
        return Task.FromResult(JsonSerializer.Serialize(finalResult, CampusToolJsonContext.Default.CampusFamilyResult));
    }

    private static ParticipantArgs? GetSiblingWithLeastWeeks(CampusFamilyArguments args)
    {
        if (args.FamilyDiscountType != "Germa" || args.Participants.Count <= 1)
        {
            return null;
        }

        var campusParticipants = args.Participants.Where(p => p.CampusWeeks > 0).ToList();

        return campusParticipants.Count > 1
            ? campusParticipants.OrderBy(p => p.CampusWeeks).First()
            : null;
    }

    private static ParticipantPricingResult CalculateParticipantPricing(ParticipantArgs p, CampusFamilyArguments args, ParticipantArgs? siblingWithLeastWeeks)
    {
        decimal campusBasePrice = GetCampusBasePrice(p.CampusWeeks, args.IsSocio);
        decimal tecniBasePrice = GetTecnificacioBasePrice(p.TecnificacioWeeks, args.IsSocio);

        // Apply a 10% surcharge if the registration is done after May 1st
        if (args.IsAfterMay1st)
        {
            campusBasePrice *= 1.10m;
            tecniBasePrice *= 1.10m;
        }

        decimal discountMultiplier = 0m;

        // Apply Family Discounts ONLY to the Campus base price
        if (p.CampusWeeks > 0)
        {
            if (args.FamilyDiscountType == "Familia Nombrosa" || args.FamilyDiscountType == "Familia Monoparental")
            {
                discountMultiplier = 0.15m;
            }
            else if (args.FamilyDiscountType == "Germa" && p == siblingWithLeastWeeks)
            {
                discountMultiplier = 0.10m;
            }
        }

        var campusDiscountAmount = campusBasePrice * discountMultiplier;
        var finalCampusPrice = campusBasePrice - campusDiscountAmount;

        var servicesCost = (p.MenjadorDays * 10m) + (p.TardaDays * 6m);
        var excursionsCost = (decimal)p.ExcursionsCost;

        var participantTotal = finalCampusPrice + tecniBasePrice + servicesCost + excursionsCost;

        return new ParticipantPricingResult(
            Name: p.Name,
            CampusWeeks: p.CampusWeeks,
            TecnificacioWeeks: p.TecnificacioWeeks,
            CampusBasePrice: campusBasePrice,
            TecnificacioBasePrice: tecniBasePrice,
            DiscountApplied: campusDiscountAmount,
            ServicesCost: servicesCost,
            ExcursionsCost: excursionsCost,
            Total: participantTotal
        );
    }

    private static decimal GetCampusBasePrice(int weeks, bool isSocio)
    {
        var clampedWeeks = Math.Clamp(weeks, 0, 6);
        if (clampedWeeks == 0) return 0m;

        if (isSocio)
        {
            return clampedWeeks switch
            {
                1 => 63.00m,
                2 => 125.00m,
                3 => 188.00m,
                4 => 244.00m,
                5 => 290.00m,
                6 => 322.00m,
                _ => 0m
            };
        }
        else
        {
            return clampedWeeks switch
            {
                1 => 69.00m,
                2 => 138.00m,
                3 => 206.00m,
                4 => 268.00m,
                5 => 320.00m,
                6 => 354.00m,
                _ => 0m
            };
        }
    }

    private static decimal GetTecnificacioBasePrice(int weeks, bool isSocio)
    {
        var clampedWeeks = Math.Clamp(weeks, 0, 4);
        if (clampedWeeks == 0) return 0m;

        if (isSocio)
        {
            return clampedWeeks switch
            {
                1 => 50.00m,
                2 => 95.00m,
                3 => 140.00m,
                4 => 185.00m,
                _ => 0m
            };
        }
        else
        {
            return clampedWeeks switch
            {
                1 => 55.00m,
                2 => 105.00m,
                3 => 154.00m,
                4 => 204.00m,
                _ => 0m
            };
        }
    }
}

public record CampusFamilyArguments(
    [property: JsonPropertyName("isSocio")] bool IsSocio,
    [property: JsonPropertyName("familyDiscountType")] string FamilyDiscountType,
    [property: JsonPropertyName("isAfterMay1st")] bool IsAfterMay1st,
    [property: JsonPropertyName("participants")] List<ParticipantArgs> Participants
);

public record ParticipantArgs(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("campusWeeks")] int CampusWeeks,
    [property: JsonPropertyName("tecnificacioWeeks")] int TecnificacioWeeks,
    [property: JsonPropertyName("menjadorDays")] int MenjadorDays,
    [property: JsonPropertyName("tardaDays")] int TardaDays,
    [property: JsonPropertyName("excursionsCost")] int ExcursionsCost
);

public record CampusFamilyResult(
    [property: JsonPropertyName("familyGrandTotal")] decimal FamilyGrandTotal,
    [property: JsonPropertyName("breakdown")] List<ParticipantPricingResult> Breakdown
);

public record ParticipantPricingResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("campusWeeks")] int CampusWeeks,
    [property: JsonPropertyName("tecnificacioWeeks")] int TecnificacioWeeks,
    [property: JsonPropertyName("campusBasePrice")] decimal CampusBasePrice,
    [property: JsonPropertyName("tecnificacioBasePrice")] decimal TecnificacioBasePrice,
    [property: JsonPropertyName("discountApplied")] decimal DiscountApplied,
    [property: JsonPropertyName("servicesCost")] decimal ServicesCost,
    [property: JsonPropertyName("excursionsCost")] decimal ExcursionsCost,
    [property: JsonPropertyName("total")] decimal Total
);