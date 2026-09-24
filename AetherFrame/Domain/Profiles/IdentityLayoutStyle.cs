using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherFrame.Domain.Profiles;

/// <summary>
/// What an Identity Header layout did to the title's style when it was chosen: the values it
/// applied, and the values they replaced. Leaving the layout restores a property only while it
/// still holds the applied value, so the layout's look is fully reversible while any change the
/// user made in between (in Basic or the Advanced editor) is kept. See
/// <see cref="BasicIdentityHeader.LayoutStyle"/>.
/// </summary>
public sealed class IdentityLayoutStyle
{
    /// <summary>The layout that applied <see cref="Applied"/>.</summary>
    public IdentityTitleLayout Layout { get; set; }

    public TitleStyleValues Applied { get; set; } = new();

    public TitleStyleValues Previous { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public IdentityLayoutStyle Clone() => new()
    {
        Layout = Layout,
        Applied = Applied.Clone(),
        Previous = Previous.Clone(),
        ExtensionData = ProfileElement.CopyExtensionData(ExtensionData),
    };

    public bool ContentEquals(IdentityLayoutStyle? other) =>
        other is not null && Layout == other.Layout && Applied.ContentEquals(other.Applied) && Previous.ContentEquals(other.Previous);
}

/// <summary>A partial set of title style values: null means "not part of this look".</summary>
public sealed class TitleStyleValues
{
    public float? FontSize { get; set; }

    public bool? Bold { get; set; }

    public bool? Italic { get; set; }

    public float? LetterSpacing { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>The values of <paramref name="title"/> for exactly the properties this set names.</summary>
    public TitleStyleValues CaptureFrom(TextProfileElement title) => new()
    {
        FontSize = FontSize is null ? null : title.FontSize,
        Bold = Bold is null ? null : title.Bold,
        Italic = Italic is null ? null : title.Italic,
        LetterSpacing = LetterSpacing is null ? null : title.LetterSpacing,
    };

    /// <summary>Sets every named property on <paramref name="title"/>.</summary>
    public void ApplyTo(TextProfileElement title)
    {
        if (FontSize is { } size)
        {
            title.FontSize = size;
        }

        if (Bold is { } bold)
        {
            title.Bold = bold;
        }

        if (Italic is { } italic)
        {
            title.Italic = italic;
        }

        if (LetterSpacing is { } spacing)
        {
            title.LetterSpacing = spacing;
        }
    }

    /// <summary>
    /// For each property this set names: when <paramref name="title"/> still holds this set's value,
    /// set it back to <paramref name="previous"/>'s. A property changed since is left alone.
    /// </summary>
    public void RevertOn(TextProfileElement title, TitleStyleValues previous)
    {
        if (FontSize is { } size && Same(title.FontSize, size) && previous.FontSize is { } oldSize)
        {
            title.FontSize = oldSize;
        }

        if (Bold is { } bold && title.Bold == bold && previous.Bold is { } oldBold)
        {
            title.Bold = oldBold;
        }

        if (Italic is { } italic && title.Italic == italic && previous.Italic is { } oldItalic)
        {
            title.Italic = oldItalic;
        }

        if (LetterSpacing is { } spacing && Same(title.LetterSpacing, spacing) && previous.LetterSpacing is { } oldSpacing)
        {
            title.LetterSpacing = oldSpacing;
        }

        static bool Same(float a, float b) => Math.Abs(a - b) < 0.001f;
    }

    public TitleStyleValues Clone() => new()
    {
        FontSize = FontSize,
        Bold = Bold,
        Italic = Italic,
        LetterSpacing = LetterSpacing,
        ExtensionData = ProfileElement.CopyExtensionData(ExtensionData),
    };

    public bool ContentEquals(TitleStyleValues? other) =>
        other is not null && FontSize == other.FontSize && Bold == other.Bold && Italic == other.Italic && LetterSpacing == other.LetterSpacing;
}
