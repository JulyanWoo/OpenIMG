using OpenIMG.Core.Models;

namespace OpenIMG.Application.Policies;

public static class ReferenceScopePolicy
{
    public const double StructuralCompatibilityMinimum = 0.80;

    public static ReferencePermissionScope Create(string referenceImageId, string role, double structuralCompatibility, string reason)
    {
        var allowed = role switch
        {
            "MaterialColorReference" => new[] { "MaterialIdentity", "FurnitureColorIdentity", "SceneColorIdentity" },
            "HardwareIdentityReference" => new[] { "HardwareIdentity", "ComponentExistence" },
            _ => Array.Empty<string>()
        };
        var structural = structuralCompatibility >= StructuralCompatibilityMinimum;
        return new ReferencePermissionScope
        {
            ReferenceImageId = referenceImageId,
            ReferenceRole = role,
            // A weakly registered view must not reach the provider as a usable
            // alternate scene. Material is transported as a silhouette-free
            // swatch; hardware is transported as a tight identity crop.
            TransportMode = structural
                ? "FullImage"
                : role == "MaterialColorReference" ? "MaterialSwatch" : "HardwareIdentityCrop",
            StructuralCompatibility = structuralCompatibility,
            AllowedScopes = allowed.Concat(structural ? new[] { "StructuralGeometry" } : Array.Empty<string>()).Distinct().ToList(),
            ForbiddenScopes = structural ? ["CameraComposition", "CurrentViewGeometry"] : ["StructuralGeometry", "CameraComposition", "CurrentViewGeometry"],
            SelectionReason = structural ? reason + "; strong primary-view compatibility" : reason + "; structural scope disabled conservatively"
        };
    }
}
