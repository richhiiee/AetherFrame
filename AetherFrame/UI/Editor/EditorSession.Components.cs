using System;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;

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

    /// <summary>Advanced: adds a Component of a built-in definition. Returns its id, or null on failure.</summary>
    internal Guid? AddComponent(string definitionId)
    {
        Guid? added = null;
        var applied = ApplyDocumentEdit(() =>
        {
            var definition = BuiltInComponentCatalog.Find(definitionId) ?? throw new ArgumentException($"Unknown Component '{definitionId}'.", nameof(definitionId));
            added = PlateComponentEditor.Add(RequireProfileForComponents(), definition).Id;
        });

        return applied ? added : null;
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
            ErrorMessage = ex.Message;
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
