using System.Text.Json;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

public class OrthogonalityUnitTests
{
    [Theory]
    [InlineData("uT")]
    [InlineData("mT")]
    [InlineData("T")]
    [InlineData("")]
    public void NonzeroNanoteslaOffset_CannotBeAppliedAsAnUnscaledDifferentUnit(string inputUnit)
    {
        var profile = new OrthogonalityParams { Unit = "nT", Offset = [100, 200, 300] };
        Assert.Throws<ArgumentException>(() => profile.ValidateUnit(inputUnit));
        profile.ValidateUnit("nT");
        Assert.Equal(new double[] { 1, 2, 3 }, profile.Apply(101, 202, 303));
    }

    [Theory]
    [InlineData("uT", "µT")]
    [InlineData("µT", "μT")]
    [InlineData("μT", "uT")]
    public void MicroteslaAliases_AreCompatibleWithoutNumericRescaling(string profileUnit, string inputUnit)
    {
        var profile = new OrthogonalityParams { Unit = profileUnit, Offset = [1, 2, 3] };
        profile.ValidateUnit(inputUnit);
        Assert.Equal("uT", OrthogonalityParams.CanonicalUnit(profileUnit));
        Assert.Equal(new double[] { 9, 18, 27 }, profile.Apply(10, 20, 30));
    }

    [Fact]
    public void JsonRoundTrip_PreservesExplicitUnitWhileLegacyMissingUnitRemainsUnknown()
    {
        var profile = new OrthogonalityParams { Id = "profile", Unit = "mT", Offset = [1, 2, 3] };
        var restored = JsonSerializer.Deserialize<OrthogonalityParams>(JsonSerializer.Serialize(profile))!;
        Assert.Equal("mT", restored.Unit);
        restored.ValidateUnit("mT");
        Assert.Throws<ArgumentException>(() => restored.ValidateUnit("nT"));

        var legacy = JsonSerializer.Deserialize<OrthogonalityParams>("""{"Id":"legacy","Offset":[1,2,3]}""")!;
        Assert.Equal("", legacy.Unit);
        Assert.Throws<ArgumentException>(() => legacy.ValidateUnit("nT"));
        Assert.Contains("\"Unit\":\"\"", JsonSerializer.Serialize(legacy));
    }

    [Fact]
    public void SnapshotFingerprint_IncludesBothProfileUnitsAndCanonicalizesAliases()
    {
        static OrthogonalityCorrectionSnapshot Snapshot(string first, string second) => new(
            new OrthogonalityParams { Id = "first", Unit = first, Offset = [1, 2, 3] },
            new OrthogonalityParams { Id = "second", Unit = second, Offset = [4, 5, 6] },
            new[] { 0, 1, 2 }, new[] { 3, 4, 5 });
        var baseline = Snapshot("nT", "nT").VersionId;
        Assert.NotEqual(baseline, Snapshot("uT", "nT").VersionId);
        Assert.NotEqual(baseline, Snapshot("nT", "mT").VersionId);
        Assert.NotEqual(baseline, Snapshot("", "nT").VersionId);
        Assert.Equal(Snapshot("uT", "µT").VersionId, Snapshot("μT", "uT").VersionId);
    }

    [Fact]
    public void Fitting_RecordsTheExplicitDataUnitWithoutInventingALegacyDefault()
    {
        var data = new double[600, 3];
        var random = new Random(42);
        for (var i = 0; i < data.GetLength(0); i++)
        {
            var z = 2 * random.NextDouble() - 1;
            var angle = 2 * Math.PI * random.NextDouble();
            var radius = Math.Sqrt(1 - z * z);
            data[i, 0] = 50 * radius * Math.Cos(angle) + 0.1;
            data[i, 1] = 50 * radius * Math.Sin(angle) - 0.05;
            data[i, 2] = 50 * z + 0.2;
        }
        var calculator = new OrthogonalityCalculator();
        var explicitUnit = calculator.Calculate(data, unit: "µT");
        Assert.True(explicitUnit.Success, explicitUnit.ErrorMessage);
        Assert.Equal("uT", explicitUnit.Parameters.Unit);
        Assert.InRange(explicitUnit.Parameters.Offset[0], -0.4, 0.6);
        Assert.InRange(explicitUnit.Parameters.Offset[1], -0.55, 0.45);
        Assert.InRange(explicitUnit.Parameters.Offset[2], -0.3, 0.7);
        explicitUnit.Parameters.ValidateUnit("μT");
        var legacy = calculator.Calculate(data);
        Assert.True(legacy.Success, legacy.ErrorMessage);
        Assert.Equal("", legacy.Parameters.Unit);
        Assert.Throws<ArgumentException>(() => legacy.Parameters.ValidateUnit("nT"));
    }
}
