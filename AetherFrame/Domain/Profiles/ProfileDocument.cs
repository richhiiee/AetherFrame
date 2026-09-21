using System;
using System.Collections.Generic;

namespace AetherFrame.Domain.Profiles;

public sealed class ProfileDocument
{
    public const int MaxElementCount = 256;

    // Logical canvas size; element Position/Size are expressed in this space, independent of
    // the edit window's actual pixel size or the editor's current zoom level.
    public const float CanvasWidth = 1920f;
    public const float CanvasHeight = 1080f;

    public int Version { get; set; } = 1;

    public Guid ProfileId { get; set; }

    public ulong OwnerContentId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Revision { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<ProfileElement> Elements { get; set; } = new();

    /// <summary>
    /// Managed asset id of the profile's background image, or null for none. Rendered behind
    /// every <see cref="ProfileElement"/>, not selectable via ordinary canvas hit testing, and
    /// tracked separately from element Z order.
    /// </summary>
    public Guid? BackgroundAssetId { get; set; }

    public BackgroundFitMode BackgroundFitMode { get; set; } = BackgroundFitMode.Cover;

    public float BackgroundOpacity { get; set; } = 1f;
}

public enum BackgroundFitMode
{
    Cover,
    Contain,
    Stretch,
}
