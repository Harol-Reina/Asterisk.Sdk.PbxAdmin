using FluentAssertions;
using PbxAdmin.LoadTests.CallGeneration;

namespace PbxAdmin.Tests.LoadTests;

public sealed class ColombianNumberGeneratorTests
{
    private static readonly HashSet<string> ValidMobilePrefixes =
    [
        "310","311","312","313","314","320","321","322","323", // Claro
        "315","316","317","318",                               // Movistar
        "300","301","302","303","304",                         // Tigo
        "350","351"                                            // WOM
    ];

    private static readonly HashSet<string> ValidLandlinePrefixes = ["601", "604", "602"];

    private readonly ColombianNumberGenerator _sut = new();

    [Fact]
    public void Generate_ShouldReturnValidMobileFormat()
    {
        // Run several times to cover both branches; confirm at least one mobile is well-formed
        CallerProfile? mobile = null;
        for (int i = 0; i < 200; i++)
        {
            var profile = _sut.Generate();
            if (profile.Type == CallerType.Mobile)
            {
                mobile = profile;
                break;
            }
        }

        mobile.Should().NotBeNull("expected at least one mobile in 200 samples");
        mobile!.Number.Should().HaveLength(12);
        mobile.Number.Should().StartWith("57");
        mobile.Number[2].Should().Be('3');
    }

    [Fact]
    public void Generate_ShouldReturnValidLandlineFormat()
    {
        CallerProfile? landline = null;
        for (int i = 0; i < 200; i++)
        {
            var profile = _sut.Generate();
            if (profile.Type == CallerType.Landline)
            {
                landline = profile;
                break;
            }
        }

        landline.Should().NotBeNull("expected at least one landline in 200 samples");
        landline!.Number.Should().HaveLength(12);
        landline.Number.Should().StartWith("57");
        landline.Number.Substring(2, 2).Should().Be("60");
    }

    [Fact]
    public void GenerateMobile_ShouldAlwaysReturnMobile()
    {
        for (int i = 0; i < 100; i++)
        {
            var profile = _sut.GenerateMobile();
            profile.Type.Should().Be(CallerType.Mobile);
        }
    }

    [Fact]
    public void GenerateLandline_ShouldAlwaysReturnLandline()
    {
        for (int i = 0; i < 100; i++)
        {
            var profile = _sut.GenerateLandline();
            profile.Type.Should().Be(CallerType.Landline);
        }
    }

    [Fact]
    public void GenerateBatch_ShouldReturnRequestedCount()
    {
        var batch = _sut.GenerateBatch(50);
        batch.Should().HaveCount(50);
    }

    [Fact]
    public void Generate_ShouldRespectMobileLandlineWeight()
    {
        const int samples = 1000;
        int mobileCount = 0;

        for (int i = 0; i < samples; i++)
        {
            if (_sut.Generate().Type == CallerType.Mobile)
                mobileCount++;
        }

        double mobileRatio = (double)mobileCount / samples;
        mobileRatio.Should().BeApproximately(0.70, 0.10,
            "default weight is 70% mobile with ±10% tolerance");
    }

    [Fact]
    public void Generate_ShouldUseAllOperatorPrefixes()
    {
        var operatorsFound = new HashSet<string>();

        for (int i = 0; i < 1000; i++)
        {
            var profile = _sut.Generate();
            operatorsFound.Add(profile.Operator);
        }

        operatorsFound.Should().Contain("Claro");
        operatorsFound.Should().Contain("Movistar");
        operatorsFound.Should().Contain("Tigo");
        operatorsFound.Should().Contain("WOM");
        // Landline cities should also appear
        operatorsFound.Should().Contain("Bogota");
    }

    [Fact]
    public void Generate_ShouldProduceValidDisplayNames()
    {
        for (int i = 0; i < 50; i++)
        {
            var profile = _sut.Generate();
            profile.DisplayName.Should().NotBeNullOrWhiteSpace();
            profile.DisplayName.Should().Contain(" ", "display name must contain first and last name separated by a space");
        }
    }

    [Fact]
    public void GenerateMobile_ShouldOnlyUseValidPrefixes()
    {
        for (int i = 0; i < 100; i++)
        {
            var profile = _sut.GenerateMobile();
            profile.Number.Should().HaveLength(12);
            string prefix = profile.Number.Substring(2, 3);
            ValidMobilePrefixes.Should().Contain(prefix,
                $"prefix '{prefix}' is not in the valid Colombian mobile prefix set");
        }
    }

    [Fact]
    public void GenerateLandline_ShouldOnlyUseValidPrefixes()
    {
        for (int i = 0; i < 100; i++)
        {
            var profile = _sut.GenerateLandline();
            profile.Number.Should().HaveLength(12);
            string prefix = profile.Number.Substring(2, 3);
            ValidLandlinePrefixes.Should().Contain(prefix,
                $"prefix '{prefix}' is not in the valid Colombian landline prefix set");
        }
    }
}
