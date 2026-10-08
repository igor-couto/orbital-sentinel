using System.Globalization;
using OrbitalSentinel.Core;

namespace OrbitalSentinel.UnitTests;

public sealed class OrbitalElementsTests
{
    [Fact]
    public void Fingerprint_IsIndependentOfCultureTimeZoneAndSignedZero()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var elements = ReferenceOrbit.Elements;
            var expected = elements.ComputeFingerprint();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-PT");
            var equivalent = elements with
            {
                EpochUtc = elements.EpochUtc.ToOffset(TimeSpan.FromHours(2)),
                MeanMotionDdot = -0d
            };
            Assert.Equal(expected, equivalent.ComputeFingerprint());
            Assert.Equal(64, expected.Length);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Fingerprint_IncludesEveryElementAndSubTlePrecision()
    {
        var elements = ReferenceOrbit.Elements;
        OrbitalElements[] changes =
        [
            elements with { EpochUtc = elements.EpochUtc.AddTicks(1) },
            elements with { MeanMotionRevolutionsPerDay = elements.MeanMotionRevolutionsPerDay + 1e-10 },
            elements with { Eccentricity = elements.Eccentricity + 1e-10 },
            elements with { InclinationDegrees = elements.InclinationDegrees + 1e-8 },
            elements with { RightAscensionDegrees = elements.RightAscensionDegrees + 1e-8 },
            elements with { ArgumentOfPericenterDegrees = elements.ArgumentOfPericenterDegrees + 1e-8 },
            elements with { MeanAnomalyDegrees = elements.MeanAnomalyDegrees + 1e-8 },
            elements with { BStar = elements.BStar + 1e-12 },
            elements with { MeanMotionDot = elements.MeanMotionDot + 1e-12 },
            elements with { MeanMotionDdot = 1e-12 }
        ];

        Assert.All(changes, changed => Assert.NotEqual(elements.ComputeFingerprint(), changed.ComputeFingerprint()));
    }

    public static TheoryData<OrbitalElements> InvalidElements => new()
    {
        ReferenceOrbit.Elements with { EpochUtc = default },
        ReferenceOrbit.Elements with { MeanMotionRevolutionsPerDay = 0 },
        ReferenceOrbit.Elements with { MeanMotionRevolutionsPerDay = double.PositiveInfinity },
        ReferenceOrbit.Elements with { Eccentricity = 1 },
        ReferenceOrbit.Elements with { Eccentricity = -0.01 },
        ReferenceOrbit.Elements with { InclinationDegrees = 180.1 },
        ReferenceOrbit.Elements with { InclinationDegrees = -1 },
        ReferenceOrbit.Elements with { RightAscensionDegrees = 360 },
        ReferenceOrbit.Elements with { ArgumentOfPericenterDegrees = -1 },
        ReferenceOrbit.Elements with { MeanAnomalyDegrees = double.NaN },
        ReferenceOrbit.Elements with { BStar = double.NegativeInfinity },
        ReferenceOrbit.Elements with { MeanMotionDot = double.NaN },
        ReferenceOrbit.Elements with { MeanMotionDdot = double.NaN }
    };

    [Theory]
    [MemberData(nameof(InvalidElements))]
    public void ValidationRejectsInvalidElements(OrbitalElements elements) =>
        Assert.ThrowsAny<ArgumentException>(elements.Validate);

    [Fact]
    public void ValidationAllowsCircularEquatorialRetrogradeAndNegativeDragFits()
    {
        (ReferenceOrbit.Elements with { Eccentricity = 0, InclinationDegrees = 0, BStar = -0.00001 }).Validate();
        (ReferenceOrbit.Elements with { InclinationDegrees = 180 }).Validate();
    }
}
