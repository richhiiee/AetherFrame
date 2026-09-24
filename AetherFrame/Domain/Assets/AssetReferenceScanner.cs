using System;
using System.Collections.Generic;
using System.Text.Json;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Assets;

/// <summary>Finds every managed asset a document references, in every place one can appear.</summary>
public static class AssetReferenceScanner
{
    public static void Collect(ProfileDocument document, ISet<Guid> into)
    {
        foreach (var element in document.Elements)
        {
            if (element is ImageProfileElement { AssetId: var assetId } && assetId != Guid.Empty)
            {
                into.Add(assetId);
            }
        }

        if (document.Background?.ImageAssetId is { } backgroundAssetId && backgroundAssetId != Guid.Empty)
        {
            // Kept even when the background isn't in Image mode: switching back restores it.
            into.Add(backgroundAssetId);
        }

        // Components drawn from an image (whatever their definition: one this build doesn't have
        // may still come back, e.g. after an update, and must find its image).
        if (document.Components is { } components)
        {
            foreach (var component in components)
            {
                if (component?.AssetId is { } componentAssetId && componentAssetId != Guid.Empty)
                {
                    into.Add(componentAssetId);
                }
            }
        }

        // A never-normalized legacy document still holds its background here.
        if (document.LegacyBackgroundAssetId is { } legacyAssetId && legacyAssetId != Guid.Empty)
        {
            into.Add(legacyAssetId);
        }

        // Data this build doesn't understand (a newer build's element types or properties) may
        // reference assets in ways it can't know. Conservatively, every GUID anywhere in it counts
        // as a reference: over-protecting an image costs a little disk, under-protecting loses it.
        CollectGuids(document.ExtensionData, into);
        CollectGuids(document.Background?.ExtensionData, into);
        CollectBasicIdentity(document.BasicIdentity, into);
        CollectBasicPlate(document.BasicPlate, into);
        foreach (var element in document.Elements)
        {
            CollectGuids(element.ExtensionData, into);
        }

        if (document.UnrecognizedElements is { } unrecognized)
        {
            foreach (var element in unrecognized)
            {
                CollectGuids(element, into);
            }
        }

        if (document.Components is { } known)
        {
            foreach (var component in known)
            {
                CollectGuids(component?.ExtensionData, into);
            }
        }

        if (document.UnrecognizedComponents is { } unreadable)
        {
            foreach (var component in unreadable)
            {
                CollectGuids(component, into);
            }
        }

        if (document.MalformedComponentsValue is { } malformed)
        {
            CollectGuids(malformed, into);
        }
    }

    /// <summary>
    /// Every preserved extension bag of the Identity Header, nested ones included: none of its known
    /// fields holds an asset today, but a newer build's fields anywhere in it might.
    /// </summary>
    private static void CollectBasicIdentity(BasicIdentityHeader? identity, ISet<Guid> into)
    {
        if (identity is null)
        {
            return;
        }

        CollectGuids(identity.ExtensionData, into);
        CollectGuids(identity.AppliedLayout?.ExtensionData, into);
        if (identity.LayoutStyle is { } style)
        {
            CollectGuids(style.ExtensionData, into);
            CollectGuids(style.Applied?.ExtensionData, into);
            CollectGuids(style.Previous?.ExtensionData, into);
        }
    }

    /// <summary>
    /// Every preserved extension bag of the Basic Plate settings (the settings themselves, each
    /// section placement, Active Hours). Its known fields hold no asset today (the Basic portrait is
    /// an ordinary image element), but a newer build may store one in a field this build doesn't know.
    /// </summary>
    private static void CollectBasicPlate(BasicPlateSettings? plate, ISet<Guid> into)
    {
        if (plate is null)
        {
            return;
        }

        CollectGuids(plate.ExtensionData, into);
        CollectGuids(plate.ActiveHours?.ExtensionData, into);
        if (plate.Placements is { } placements)
        {
            foreach (var placement in placements)
            {
                CollectGuids(placement?.ExtensionData, into);
            }
        }
    }

    private static void CollectGuids(Dictionary<string, JsonElement>? data, ISet<Guid> into)
    {
        if (data is null)
        {
            return;
        }

        foreach (var value in data.Values)
        {
            CollectGuids(value, into);
        }
    }

    private static void CollectGuids(JsonElement value, ISet<Guid> into)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty)
                {
                    into.Add(id);
                }

                break;

            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    CollectGuids(property.Value, into);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    CollectGuids(item, into);
                }

                break;
        }
    }

    public static HashSet<Guid> Collect(IEnumerable<ProfileDocument> documents)
    {
        var result = new HashSet<Guid>();
        foreach (var document in documents)
        {
            Collect(document, result);
        }

        return result;
    }
}
