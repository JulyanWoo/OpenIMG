using OpenIMG.Application.Policies;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class ReferenceScopePolicyTests
{
    [Fact]
    public void MaterialColorReferenceCannotAuthorizeGeometry()
    {
        var scope = ReferenceScopePolicy.Create("view-b", "MaterialColorReference", 0.20, "different viewpoint");

        Assert.Equal("MaterialColorReference", scope.ReferenceRole);
        Assert.Contains("MaterialIdentity", scope.AllowedScopes);
        Assert.Contains("FurnitureColorIdentity", scope.AllowedScopes);
        Assert.DoesNotContain("StructuralGeometry", scope.AllowedScopes);
        Assert.Contains("StructuralGeometry", scope.ForbiddenScopes);
        Assert.Equal("MaterialSwatch", scope.TransportMode);
    }

    [Fact]
    public void HardwareReferenceCanConfirmIdentityButNotPosition()
    {
        var scope = ReferenceScopePolicy.Create("view-c", "HardwareIdentityReference", 0.20, "identity only");

        Assert.Equal("HardwareIdentityReference", scope.ReferenceRole);
        Assert.Contains("HardwareIdentity", scope.AllowedScopes);
        Assert.Contains("ComponentExistence", scope.AllowedScopes);
        Assert.DoesNotContain("StructuralGeometry", scope.AllowedScopes);
        Assert.Contains("CurrentViewGeometry", scope.ForbiddenScopes);
        Assert.Equal("HardwareIdentityCrop", scope.TransportMode);
    }

    [Fact]
    public void StructuralGeometryRequiresStrongCompatibility()
    {
        var weak = ReferenceScopePolicy.Create("weak", "MaterialColorReference", 0.79, "weak registration");
        var strong = ReferenceScopePolicy.Create("strong", "MaterialColorReference", 0.80, "compatible view");

        Assert.DoesNotContain("StructuralGeometry", weak.AllowedScopes);
        Assert.Contains("StructuralGeometry", weak.ForbiddenScopes);
        Assert.Contains("StructuralGeometry", strong.AllowedScopes);
        Assert.DoesNotContain("StructuralGeometry", strong.ForbiddenScopes);
        Assert.Equal("FullImage", strong.TransportMode);
    }
}
