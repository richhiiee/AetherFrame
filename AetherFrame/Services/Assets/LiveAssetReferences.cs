using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AetherFrame.Domain.Assets;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;

namespace AetherFrame.Services.Assets;

/// <summary>
/// All live local creative asset references: Plate references union Template references,
/// including each library's own trash (per its existing retention semantics — nothing new here).
/// This is the one entry point any future cleanup/GC code must call instead of either library's
/// scan alone, so it can't accidentally scan Plates while forgetting Templates (or the reverse).
/// <see cref="AssetGarbageCollector"/> stays dormant — nothing calls <c>Plan</c> anywhere yet —
/// but this makes the eventual activation a one-line, correct-by-construction call.
/// </summary>
internal static class LiveAssetReferences
{
    internal static async Task<AssetReferenceScan> ComputeAsync(PlateLibraryService plateLibrary, TemplateLibraryService templateLibrary)
    {
        var plateScan = await plateLibrary.ScanAssetReferencesAsync().ConfigureAwait(false);
        var templateScan = await templateLibrary.ScanAssetReferencesAsync().ConfigureAwait(false);

        var referenced = new HashSet<Guid>(plateScan.ReferencedAssetIds);
        referenced.UnionWith(templateScan.ReferencedAssetIds);

        var problems = new List<string>(plateScan.Problems.Count + templateScan.Problems.Count);
        problems.AddRange(plateScan.Problems);
        problems.AddRange(templateScan.Problems);

        return new AssetReferenceScan(plateScan.IsComplete && templateScan.IsComplete, referenced, problems);
    }
}
