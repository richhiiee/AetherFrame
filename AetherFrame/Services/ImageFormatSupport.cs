using System;
using System.Collections.Generic;
using System.Linq;

namespace AetherFrame.Services;

/// <summary>
/// Restricts imported images to a small, predictable set of formats, but only ever from among
/// the ones the current Dalamud texture pipeline actually reports as decodable (see
/// <c>ITextureProvider.GetSupportedImageDecoderInfos</c>) — never assumed at build time.
/// </summary>
internal static class ImageFormatSupport
{
    // Extensions are compared without the leading '.', case-insensitively, since it's not
    // documented whether ITextureProvider reports them with or without one.
    private static readonly string[] PreferredExtensions = ["png", "jpg", "jpeg", "webp"];

    private static IReadOnlyList<string>? cachedSupportedExtensions;

    internal static IReadOnlyList<string> SupportedExtensions =>
        cachedSupportedExtensions ??= ComputeSupportedExtensions();

    internal static bool IsSupported(string fileExtension) =>
        SupportedExtensions.Contains(Normalize(fileExtension), StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds an ImGuiFileDialog filter string, e.g. "Image Files{.png,.jpg,.webp}".</summary>
    internal static string BuildFileDialogFilter() =>
        $"Image Files{{{string.Join(',', SupportedExtensions.Select(ext => "." + ext))}}}";

    private static List<string> ComputeSupportedExtensions()
    {
        var decoderExtensions = DalamudServices.TextureProvider.GetSupportedImageDecoderInfos()
            .SelectMany(info => info.Extensions)
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return PreferredExtensions.Where(decoderExtensions.Contains).ToList();
    }

    private static string Normalize(string extension) =>
        (extension.StartsWith('.') ? extension[1..] : extension).ToLowerInvariant();
}
