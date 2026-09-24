using System;
using System.Collections.Generic;

namespace AetherFrame.Domain.Assets;

/// <summary>Every asset referenced by a set of documents (a Plate Library or Template Library
/// scan), including trashed ones. Only a complete scan (every document readable) may ever be used
/// to decide an asset is unreferenced.</summary>
public sealed record AssetReferenceScan(bool IsComplete, IReadOnlySet<Guid> ReferencedAssetIds, IReadOnlyList<string> Problems);
