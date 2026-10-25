namespace Il2Radio.Core.Radio;

/// <summary>
/// How IL-2's own message line looks, measured from an in-game screenshot of "Game is Paused"
/// (2026-10-25): one translucent dark box per message, sized to its text, with plain light-grey
/// sans-serif text and no coloured speaker. The box darkens the terrain behind it from 0x78 to
/// 0x46 (×0.58), i.e. black at about 42 % opacity. Proportions are in cap heights (11 px in the
/// screenshot) because its scale is unknown; the absolute size comes from --chat-font.
/// </summary>
public static class Il2ChatStyle
{
    /// <summary>Double-storey "a", plain "G": an Arial-like grotesque.</summary>
    public const string FontFamily = "Arial";

    /// <summary>Arial's cap height as a fraction of its em size.</summary>
    public const float CapHeightEm = 0.716f;

    /// <summary>Brightest fully covered stroke pixels measured 0xDCDDD9–0xDFDFDB.</summary>
    public const int TextRgb = 0xE0E0DC;

    /// <summary>The tool's own notes ("no reply …") are not radio traffic, so they are dimmer.</summary>
    public const int SystemRgb = 0xA8A8A4;

    public const int BoxRgb = 0x000000;
    public const int BoxAlpha = 107; // 42 %

    /// <summary>Box height 29 px for an 11 px cap height, text centred vertically.</summary>
    public const float BoxHeightCaps = 29f / 11f;

    /// <summary>16 px from the box edge to the first glyph.</summary>
    public const float PaddingCaps = 16f / 11f;

    /// <summary>Gap between stacked boxes. Not seen in the screenshot (one message); an estimate.</summary>
    public const float GapCaps = 3f / 11f;

    /// <summary>Text colour for a line: radio traffic in IL-2's message colour, tool notes dimmer.</summary>
    public static int Colour(ChatLine line) => line.Kind == ChatLineKind.System ? SystemRgb : TextRgb;
}
