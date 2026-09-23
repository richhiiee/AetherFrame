using System;
using System.Collections.Generic;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Plates;

/// <summary>The starting points Create Plate offers. Not Templates: nothing references the
/// layout after creation — it only decides the new document's initial content.</summary>
public enum PlateStartingLayout
{
    /// <summary>The Adventure Plate canvas with a theme background, opened in the Basic Editor.</summary>
    AdventurePlateClassic,

    /// <summary>The Adventure Plate canvas with no background, opened in the Advanced Editor.</summary>
    Blank,
}

public static class PlateFactory
{
    public static string DefaultNameFor(PlateStartingLayout layout) => layout switch
    {
        PlateStartingLayout.AdventurePlateClassic => "Adventure Plate",
        _ => "Blank Plate",
    };

    /// <summary>Whether a new Plate from this layout opens in the Basic Editor (else Advanced).</summary>
    public static bool OpensInBasicEditor(PlateStartingLayout layout) => layout == PlateStartingLayout.AdventurePlateClassic;

    /// <summary>
    /// A complete, valid document ready to save: current schema version, the Adventure Plate
    /// canvas, and a resolved background, so no legacy repair ever applies to it. Carries no
    /// character identity (<see cref="ProfileDocument.OwnerContentId"/> stays 0).
    /// </summary>
    public static ProfileDocument Create(PlateStartingLayout layout, Guid plateId, string name, DateTime nowUtc)
    {
        var background = new ProfileBackground();
        if (layout == PlateStartingLayout.AdventurePlateClassic)
        {
            ProfileThemePresets.All[0].ApplyTo(background);
        }

        return new ProfileDocument
        {
            Version = ProfileDocument.CurrentSchemaVersion,
            ProfileId = plateId,
            OwnerContentId = 0,
            Name = name,
            Revision = 0,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            CanvasWidth = ProfileCanvasPreset.AdventurePlate.Width,
            CanvasHeight = ProfileCanvasPreset.AdventurePlate.Height,
            Background = background,
            Elements = new List<ProfileElement>(),
        };
    }
}
