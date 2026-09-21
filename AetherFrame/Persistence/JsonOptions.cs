using System.Text.Json;

namespace AetherFrame.Persistence;

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,

        // System.Numerics.Vector4 (used for TextProfileElement.Color) exposes X/Y/Z/W as
        // public fields, not properties, so they're silently dropped without this.
        IncludeFields = true,
    };
}
