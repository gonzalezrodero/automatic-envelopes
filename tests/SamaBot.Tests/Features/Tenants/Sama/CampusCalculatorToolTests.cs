using AwesomeAssertions;
using SamaBot.Api.Features.Tenants.Sama.CampusPricing;
using System.Text.Json;

namespace SamaBot.Tests.Features.Tenants.Sama;

public class CampusCalculatorToolTests
{
    private readonly CampusCalculatorTool _sut = new();

    [Fact]
    public void GetSpecification_ReturnsValidToolSchema()
    {
        // Act
        var spec = _sut.GetSpecification();

        // Assert
        spec.Name.Should().Be("calculate_family_campus_price");
        spec.InputSchema.Json.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_CalculatesCorrectPrices_WithMixedProgramsAndDiscounts()
    {
        // Arrange: Socio, Nombrosa, 1 child in Campus + Menjador, 1 child in Tecnificacio
        var payload = """
        {
            "isSocio": true,
            "familyDiscountType": "Familia Nombrosa",
            "participants": [
                {
                    "name": "Lucas",
                    "campusWeeks": 2,
                    "tecnificacioWeeks": 0,
                    "menjadorDays": 5,
                    "tardaDays": 0,
                    "excursionsCost": 0
                },
                {
                    "name": "Sister",
                    "campusWeeks": 0,
                    "tecnificacioWeeks": 1,
                    "menjadorDays": 0,
                    "tardaDays": 0,
                    "excursionsCost": 35
                }
            ]
        }
        """;

        // Act
        var jsonResult = await _sut.ExecuteAsync(payload, CancellationToken.None);

        // Assert
        var result = JsonSerializer.Deserialize(jsonResult, CampusToolJsonContext.Default.CampusFamilyResult);

        result.Should().NotBeNull();

        // Lucas: Base 125 - 15% (18.75) + Menjador (50) = 156.25
        var lucas = result.Breakdown.First(p => p.Name == "Lucas");
        lucas.CampusBasePrice.Should().Be(125.00m);
        lucas.DiscountApplied.Should().Be(18.75m);
        lucas.Total.Should().Be(156.25m);

        // Sister: TecniBase 50 + Excursion 35 = 85 (No discounts on Tecnificacio)
        var sister = result.Breakdown.First(p => p.Name == "Sister");
        sister.TecnificacioBasePrice.Should().Be(50.00m);
        sister.DiscountApplied.Should().Be(0m);
        sister.Total.Should().Be(85.00m);

        result.FamilyGrandTotal.Should().Be(156.25m + 85.00m);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenArgumentsInvalidOrEmpty()
    {
        // Act - Testing null/empty participants array
        var result1 = await _sut.ExecuteAsync("{\"isSocio\": true, \"participants\": []}", CancellationToken.None);
        var result2 = await _sut.ExecuteAsync("{}", CancellationToken.None); // Missing properties entirely

        // Assert
        result1.Should().Be("Error: Invalid arguments.");
        result2.Should().Be("Error: Invalid arguments.");
    }

    [Fact]
    public async Task ExecuteAsync_AppliesGermaDiscount_OnlyToSiblingWithLeastWeeks()
    {
        // Arrange: Socio, Germa discount. 
        // Child 1: 3 weeks. Child 2: 1 week. Child 3 (Tecnificacio only): 2 weeks.
        // Child 2 should get the 10% discount on Campus. Child 1 and 3 get 0%.
        var payload = """
        {
            "isSocio": true,
            "familyDiscountType": "Germa",
            "participants": [
                { "name": "Older", "campusWeeks": 3, "tecnificacioWeeks": 0, "menjadorDays": 0, "tardaDays": 0, "excursionsCost": 0 },
                { "name": "Younger", "campusWeeks": 1, "tecnificacioWeeks": 0, "menjadorDays": 0, "tardaDays": 0, "excursionsCost": 0 },
                { "name": "TecniOnly", "campusWeeks": 0, "tecnificacioWeeks": 2, "menjadorDays": 0, "tardaDays": 0, "excursionsCost": 0 }
            ]
        }
        """;

        // Act
        var jsonResult = await _sut.ExecuteAsync(payload, CancellationToken.None);
        var result = JsonSerializer.Deserialize<CampusFamilyResult>(jsonResult, CampusToolJsonContext.Default.CampusFamilyResult);

        // Assert
        var older = result!.Breakdown.First(p => p.Name == "Older");
        older.CampusBasePrice.Should().Be(188.00m);
        older.DiscountApplied.Should().Be(0m); // No discount

        var younger = result.Breakdown.First(p => p.Name == "Younger");
        younger.CampusBasePrice.Should().Be(63.00m);
        younger.DiscountApplied.Should().Be(6.30m); // 10% of 63

        var tecni = result.Breakdown.First(p => p.Name == "TecniOnly");
        tecni.DiscountApplied.Should().Be(0m); // Tecnificacio never gets Germa discount
    }

    [Fact]
    public async Task ExecuteAsync_UsesNonSocioTablesAndClampsWeeksCorrectly()
    {
        // Arrange: NON-Socio, No discounts.
        // We will pass 10 weeks of Campus (should clamp to 6) and -2 weeks of Tecni (should clamp to 0)
        var payload = """
        {
            "isSocio": false,
            "familyDiscountType": "None",
            "participants": [
                {
                    "name": "LimitTester",
                    "campusWeeks": 10,
                    "tecnificacioWeeks": -2,
                    "menjadorDays": 1,
                    "tardaDays": 1,
                    "excursionsCost": 0
                }
            ]
        }
        """;

        // Act
        var jsonResult = await _sut.ExecuteAsync(payload, CancellationToken.None);
        var result = JsonSerializer.Deserialize<CampusFamilyResult>(jsonResult, CampusToolJsonContext.Default.CampusFamilyResult);

        // Assert
        var tester = result!.Breakdown.First();

        // Non-socio 6-week max clamp is 354.00
        tester.CampusBasePrice.Should().Be(354.00m);

        // Tecni -2 weeks clamps to 0 weeks = 0.00
        tester.TecnificacioBasePrice.Should().Be(0m);

        tester.DiscountApplied.Should().Be(0m);
        tester.ServicesCost.Should().Be(16.00m); // 1 Menjador (10) + 1 Tarda (6)
    }
}