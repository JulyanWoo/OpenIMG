namespace OpenIMG.Core.Models;

public sealed class ReferencePermissionScope
{
    public string ReferenceImageId { get; set; } = string.Empty;
    public string ReferenceRole { get; set; } = string.Empty;
    public string TransportMode { get; set; } = "FullImage";
    public string TransportPath { get; set; } = string.Empty;
    public List<string> AllowedScopes { get; set; } = [];
    public List<string> ForbiddenScopes { get; set; } = [];
    public double StructuralCompatibility { get; set; }
    public string SelectionReason { get; set; } = string.Empty;
}

public sealed class SharedMaterialAssignment
{
    public string MaterialFamilyId { get; set; } = string.Empty;
    public string ObservedValue { get; set; } = string.Empty;
    public List<string> ComponentKinds { get; set; } = [];
    public double Confidence { get; set; }
    public string Authority { get; set; } = "Advisory";
    public List<string> SupportingViews { get; set; } = [];
}
