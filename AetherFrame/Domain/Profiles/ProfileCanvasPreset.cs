namespace AetherFrame.Domain.Profiles;

/// <summary>
/// A named canvas size offered by the Advanced Editor's canvas size control. Purely a UI
/// convenience over <see cref="ProfileDocument.CanvasWidth"/>/<see cref="ProfileDocument.CanvasHeight"/>
/// — a profile's canvas is never required to match one of these; any size a preset was applied
/// from remains ordinary persisted data once saved.
/// </summary>
public sealed record ProfileCanvasPreset(string Name, float Width, float Height)
{
    public static readonly ProfileCanvasPreset AdventurePlate = new("Adventure Plate", ProfileDocument.DefaultCanvasWidth, ProfileDocument.DefaultCanvasHeight);

    public static readonly ProfileCanvasPreset Card = new("Card", 1200f, 800f);

    public static readonly ProfileCanvasPreset WideHd = new("Wide HD", 1600f, 900f);

    public static readonly ProfileCanvasPreset LargeCard = new("Large Card", 1800f, 1200f);

    public static readonly ProfileCanvasPreset[] All = [AdventurePlate, Card, WideHd, LargeCard];

    /// <summary>The preset matching a canvas size, or null if it doesn't match any (i.e. "Custom").</summary>
    public static ProfileCanvasPreset? Match(float width, float height)
    {
        foreach (var preset in All)
        {
            if (preset.Width.Equals(width) && preset.Height.Equals(height))
            {
                return preset;
            }
        }

        return null;
    }
}
