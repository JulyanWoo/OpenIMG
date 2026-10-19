namespace OpenIMG.Core.Models;

public enum RegionPrecedence
{
    FurnitureHardware = 1,
    FurnitureStructural = 2,
    FurnitureMaterialSensitive = 3,
    FurnitureColorSensitive = 4,
    FurnitureGlass = 5,
    FurnitureDecorative = 6,
    Uncertain = 7,
    PermanentEnvironment = 8,
    RemovableScene = 9
}

public enum OcclusionType
{
    SceneOnly,
    FurniturePartiallyOccluded,
    FurnitureHeavilyOccluded,
    Uncertain
}
