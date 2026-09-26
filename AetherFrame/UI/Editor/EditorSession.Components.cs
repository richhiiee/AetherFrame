using System;
using System.Collections.Generic;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Diagnostics;
using AetherFrame.UI.Rendering;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Component half of <see cref="EditorSession"/>, shared by the Basic and Advanced editors. Every
/// change is a document edit (<see cref="ApplyDocumentEdit"/> / <see cref="BeginOrContinueDocumentEdit"/>),
/// so it is exactly one undo step, and the dirty state sees it through the captured document state.
/// The rules themselves live in <see cref="PlateComponentEditor"/>.
/// </summary>
internal sealed partial class EditorSession
{
    /// <summary>Basic: sets a slot's style, or empties it (null). One undo step; nothing if unchanged.</summary>
    internal void SetComponentSlot(PlateComponentKind kind, string? definitionId) =>
        ApplyDocumentEdit(() => PlateComponentEditor.SetSlot(RequireProfileForComponents(), kind, definitionId, BuiltInComponentCatalog.Instance));

    /// <summary>
    /// Measures the name and title the way they are drawn, so an anchor fixed from them matches what
    /// is on screen; without one (tests, or before the fonts exist) their whole boxes are used.
    /// </summary>
    internal IIdentityTextMeasurer? IdentityMeasurer { get; set; }

    /// <summary>
    /// Advanced: adds a Component of a built-in definition. Returns its id, or null on failure. A
    /// Name Backing or Divider starts where the name and title are now, then stays there (a fixed
    /// anchor; see <see cref="PlateComponent.FixedAnchorPosition"/>): the Advanced editor moves the
    /// name and the decoration independently. The Basic slots keep following the name.
    /// </summary>
    internal Guid? AddComponent(string definitionId)
    {
        Guid? added = null;
        var applied = ApplyDocumentEdit(() =>
        {
            var definition = BuiltInComponentCatalog.Find(definitionId) ?? throw new ArgumentException($"Unknown Component '{definitionId}'.", nameof(definitionId));
            var profile = RequireProfileForComponents();
            var component = PlateComponentEditor.Add(profile, definition);
            if (ContentAnchor(profile, component.Kind) is { } anchor)
            {
                PlateComponentEditor.SetFixedAnchor(profile, component.Id, anchor);
            }

            added = component.Id;
        });

        return applied ? added : null;
    }

    /// <summary>
    /// Advanced: makes a Name Backing or Divider follow the name and title again (true), or fixes it
    /// where it is drawn now (false), without it moving. One undo step; nothing if unchanged.
    /// </summary>
    internal void SetComponentFollowsContent(Guid componentId, bool follows) =>
        ApplyDocumentEdit(() =>
        {
            var profile = RequireProfileForComponents();
            if (PlateComponentEditor.Find(profile, componentId) is { } component)
            {
                PlateComponentEditor.SetFixedAnchor(profile, componentId, follows ? null : ContentAnchor(profile, component.Kind));
            }
        });

    /// <summary>The box a Name Backing or Divider of <paramref name="kind"/> would follow right now, as
    /// the renderer places it; null for kinds that can't be fixed.</summary>
    private ElementRect? ContentAnchor(ProfileDocument profile, PlateComponentKind kind)
    {
        if (!PlateComponentEditor.CanFixAnchor(kind))
        {
            return null;
        }

        var drawn = new List<ProfileElement>();
        ProfilePaintOrder.Fill(profile, drawn, includeHidden: false);
        var measurer = IdentityMeasurer;
        Func<TextProfileElement, float?>? measure = measurer is null ? null : element => measurer.TryMeasureNaturalWidth(element, out var width) ? width : null;
        return ComponentPaintPlan.ContentAnchor(profile, drawn, kind, measure);
    }

    internal void RemoveComponent(Guid componentId) =>
        ApplyDocumentEdit(() => PlateComponentEditor.Remove(RequireProfileForComponents(), componentId));

    /// <summary>
    /// Edits one Component's settings; values are bounded after <paramref name="apply"/>.
    /// <paramref name="continuous"/> coalesces a slider or color drag into one undo step (commit
    /// with <see cref="CommitPendingDocumentEdit"/>, or implicitly by any other action).
    /// </summary>
    internal void EditComponent(Guid componentId, Action<PlateComponent> apply, bool continuous)
    {
        void Change() => PlateComponentEditor.Update(RequireProfileForComponents(), componentId, apply);

        if (continuous)
        {
            BeginOrContinueDocumentEdit(Change);
        }
        else
        {
            ApplyDocumentEdit(Change);
        }
    }

    /// <summary>Turns one corner of a Corner Ornament on or off. One undo step; nothing if unchanged
    /// or if it would leave no corner (see <see cref="PlateComponentEditor.SetCorner"/>).</summary>
    internal void SetComponentCorner(Guid componentId, CornerMask corner, bool enabled) =>
        ApplyDocumentEdit(() => PlateComponentEditor.SetCorner(RequireProfileForComponents(), componentId, corner, enabled));

    internal void MoveComponentInLayer(Guid componentId, int direction) =>
        ApplyDocumentEdit(() => PlateComponentEditor.MoveInLayer(RequireProfileForComponents(), componentId, direction));

    internal void ResetComponentTransform(Guid componentId) =>
        ApplyDocumentEdit(() => PlateComponentEditor.ResetTransform(RequireProfileForComponents(), componentId));

    /// <summary>
    /// Imports an image into managed assets and makes it this Component's image (for image
    /// definitions). Like replacing an element's image, the previous asset stays on disk for undo.
    /// </summary>
    internal void SetComponentImage(Guid componentId, string sourceFilePath)
    {
        ErrorMessage = null;

        Guid assetId;
        try
        {
            assetId = assetStorage.ImportImage(sourceFilePath);
        }
        catch (Exception ex)
        {
            ErrorMessage = UserFacingError.Describe(ex, ImageImportFailedMessage);
            return;
        }

        ApplyDocumentEdit(() => PlateComponentEditor.Update(RequireProfileForComponents(), componentId, component => component.AssetId = assetId));
    }

    private ProfileDocument RequireProfileForComponents()
    {
        profileService.RequireEditableProfile();
        return profileService.CurrentProfile ?? throw new InvalidOperationException("No Plate is open.");
    }
}
