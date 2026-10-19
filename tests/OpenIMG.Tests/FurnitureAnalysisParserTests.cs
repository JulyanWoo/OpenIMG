using OpenIMG.Application.Parsing;
using Xunit;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public class FurnitureAnalysisParserTests
{
    [Fact]
    public void CleanJsonContent_StripsMarkdownCodeFences()
    {
        var raw = "```json\n{\n  \"furnitureType\": \"closet\"\n}\n```";
        var cleaned = FurnitureAnalysisParser.CleanJsonContent(raw);

        Assert.Equal("{\n  \"furnitureType\": \"closet\"\n}", cleaned);
    }

    [Fact]
    public void CleanJsonContent_ExtractsBracesWhenSurroundedByText()
    {
        var raw = "Here is the furniture analysis:\n{\n  \"furnitureType\": \"desk\"\n}\nHope this helps!";
        var cleaned = FurnitureAnalysisParser.CleanJsonContent(raw);

        Assert.Equal("{\n  \"furnitureType\": \"desk\"\n}", cleaned);
    }

    [Fact]
    public void Parse_ValidJson_ReturnsPopulatedAnalysis()
    {
        var json = @"{
          ""furnitureType"": ""cabinet"",
          ""geometryDescription"": ""2-door base unit with top drawers"",
          ""confidence"": 0.94,
          ""mainMaterials"": [""melamine"", ""aluminum""],
          ""mainColors"": [""white""],
          ""accentColors"": [""silver""],
          ""colorLocations"": {
            ""white"": ""doors and carcass"",
            ""silver"": ""handles""
          },
          ""doors"": [""2 flush doors""],
          ""drawers"": [""2 top drawers""],
          ""protectedFeatures"": [""number of doors: exactly 2""],
          ""protectedRegions"": [
            {
              ""x"": 0.1,
              ""y"": 0.2,
              ""width"": 0.8,
              ""height"": 0.7,
              ""regionType"": ""carcass"",
              ""description"": ""main box"",
              ""importance"": 1.0
            }
          ],
          ""components"": [
            {
              ""id"": ""door_left"",
              ""parentComponentId"": ""cabinet_carcass"",
              ""type"": ""door"",
              ""description"": ""Left hinged door"",
              ""color"": ""white"",
              ""material"": ""melamine"",
              ""isStructural"": true,
              ""importance"": ""critical"",
              ""confidence"": 0.97,
              ""region"": {
                ""x"": 0.1,
                ""y"": 0.3,
                ""width"": 0.4,
                ""height"": 0.6,
                ""regionType"": ""door"",
                ""description"": ""Left door"",
                ""importance"": 1.0
              }
            }
          ]
        }";

        var analysis = FurnitureAnalysisParser.Parse(json);

        Assert.Equal("cabinet", analysis.FurnitureType);
        Assert.Equal("2-door base unit with top drawers", analysis.GeometryDescription);
        Assert.Equal(0.94, analysis.Confidence);
        Assert.Single(analysis.MainColors);
        Assert.Equal("white", analysis.MainColors[0]);
        Assert.Single(analysis.ProtectedFeatures);
        Assert.Single(analysis.ProtectedRegions);
        Assert.Equal(0.1, analysis.ProtectedRegions[0].X);
        Assert.Single(analysis.Components);

        var comp = analysis.Components[0];
        Assert.Equal("door_left", comp.Id);
        Assert.Equal("cabinet_carcass", comp.ParentComponentId);
        Assert.True(comp.IsStructural);
        Assert.NotNull(comp.Region);
        Assert.Equal(0.4, comp.Region!.Width);
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsFormatException()
    {
        var bad = "Not JSON content at all";
        Assert.Throws<FormatException>(() => FurnitureAnalysisParser.Parse(bad));
    }
}
