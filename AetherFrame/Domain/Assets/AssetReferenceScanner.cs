using System;
using System.Collections.Generic;
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

        // A never-normalized legacy document still holds its background here.
        if (document.LegacyBackgroundAssetId is { } legacyAssetId && legacyAssetId != Guid.Empty)
        {
            into.Add(legacyAssetId);
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
