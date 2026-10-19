namespace OpenIMG.Core.Enums;

public enum PhysicalAdditionCategory
{
    /// <summary>
    /// Addition directly modifying or overlapping furniture structure, panels, or components (struts, shelves, countertops).
    /// </summary>
    FurniturePhysicalAddition,

    /// <summary>
    /// Addition of a coherent permanent architectural or electrical fixture (outlet, switch box, wall fixture) on permanent scene surfaces.
    /// </summary>
    PermanentScenePhysicalAddition,

    /// <summary>
    /// Tiny, localized spot, screw mark, or minor speck outside furniture structure that does not alter permanent architectural identity.
    /// </summary>
    MinorSceneDetailAddition
}
