using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Templates;

/// <summary>One built-in Template AetherFrame ships. <see cref="TemplateId"/> is frozen forever
/// once shipped — never reused, never reassigned by an update — mirroring the same policy
/// <c>ProfileThemePresets</c> already uses for its own frozen ids. <see cref="SupportsPreview"/> is
/// a per-Template capability (e.g. Blank Canvas has nothing worth previewing) — UI that gates
/// Preview must read this, never infer it from <see cref="Name"/> or any other display string.</summary>
public sealed record BuiltInTemplateDefinition(Guid TemplateId, string Name, string Description, PlateStartingLayout Layout, bool SupportsPreview);

/// <summary>
/// The built-in Templates: Adventure Plate Classic and Blank Canvas. Never persisted as files —
/// they are regenerated fresh on every call, so "stable ids so app updates don't create
/// duplicates" holds by construction (there is nothing on disk to deduplicate). Their content is
/// produced by delegating straight into <see cref="PlateFactory.Create(PlateStartingLayout, Guid, string, DateTime, PlateStarterContent?)"/>
/// — the same code today's Create Plate flow already uses — so this IS the shared built-in
/// source of truth, not a second copy of the starter logic.
/// </summary>
public static class BuiltInTemplateCatalog
{
    public static readonly Guid AdventurePlateClassicId = Guid.Parse("2f6a1b8e-0000-4000-8000-000000000001");
    public static readonly Guid BlankCanvasId = Guid.Parse("2f6a1b8e-0000-4000-8000-000000000002");

    public static readonly IReadOnlyList<BuiltInTemplateDefinition> All =
    [
        new BuiltInTemplateDefinition(
            AdventurePlateClassicId,
            "Adventure Plate Classic",
            "A ready-to-fill Adventure Plate with every section in place, opened in the Basic editor.",
            PlateStartingLayout.AdventurePlateClassic,
            SupportsPreview: true),
        new BuiltInTemplateDefinition(
            BlankCanvasId,
            "Blank Canvas",
            "An empty Adventure Plate canvas for the Advanced editor.",
            PlateStartingLayout.Blank,
            SupportsPreview: false), // an empty canvas has nothing worth previewing
    ];

    public static BuiltInTemplateDefinition? Find(Guid templateId) => All.FirstOrDefault(d => d.TemplateId == templateId);

    public static bool IsBuiltIn(Guid templateId) => Find(templateId) is not null;

    /// <summary>
    /// A fresh document for a built-in Template: never cached, never stored. <paramref name="starter"/>
    /// only affects Adventure Plate Classic (see <see cref="PlateFactory"/>); Blank Canvas ignores it.
    /// </summary>
    public static ProfileDocument CreateDocument(Guid builtInTemplateId, DateTime nowUtc, PlateStarterContent? starter)
    {
        var definition = Find(builtInTemplateId) ?? throw new ArgumentOutOfRangeException(nameof(builtInTemplateId), "Not a built-in Template id.");
        return PlateFactory.Create(definition.Layout, Guid.Empty, definition.Name, nowUtc, starter);
    }
}
