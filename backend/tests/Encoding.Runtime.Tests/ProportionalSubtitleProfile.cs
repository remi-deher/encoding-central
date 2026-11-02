using Encoding.Contracts.Flows;

namespace Encoding.Runtime.Tests;

/// <summary>Document qui décrit l'ancien profil proportionnel. Il vit dans les tests, plus dans le moteur.</summary>
internal static class ProportionalSubtitleProfile
{
    internal const string Json = """
        {
          "formatVersion": 1,
          "profileVersion": "2",
          "reference": { "width": 640, "height": 360 },
          "script": { "title": "New subtitles", "wrapStyle": 0, "scaledBorderAndShadow": true, "ycbcrMatrix": "TV.601" },
          "styles": [
            { "name": "Default", "fontName": "Trebuchet MS", "fontSize": 23, "primaryColour": "&H00FFFFFF", "secondaryColour": "&H000000FF", "outlineColour": "&H00000000", "backColour": "&H00000000", "bold": -1, "italic": 0, "underline": 0, "strikeOut": 0, "scaleX": 100, "scaleY": 100, "spacing": 0, "angle": 0, "borderStyle": 1, "outline": 2.5, "shadow": 0, "alignment": 2, "marginL": 20, "marginR": 20, "marginV": 20, "encoding": 1 },
            { "name": "Italique", "fontName": "Trebuchet MS", "fontSize": 23, "primaryColour": "&H00FFFFFF", "secondaryColour": "&H000000FF", "outlineColour": "&H00000000", "backColour": "&H00000000", "bold": -1, "italic": -1, "underline": 0, "strikeOut": 0, "scaleX": 100, "scaleY": 100, "spacing": 0, "angle": 0, "borderStyle": 1, "outline": 2.5, "shadow": 0, "alignment": 2, "marginL": 20, "marginR": 20, "marginV": 20, "encoding": 1 },
            { "name": "TiretsDefault", "fontName": "Trebuchet MS", "fontSize": 23, "primaryColour": "&H00FFFFFF", "secondaryColour": "&H000000FF", "outlineColour": "&H00000000", "backColour": "&H00000000", "bold": -1, "italic": 0, "underline": 0, "strikeOut": 0, "scaleX": 100, "scaleY": 100, "spacing": 0, "angle": 0, "borderStyle": 1, "outline": 2.5, "shadow": 0, "alignment": 1, "marginL": 20, "marginR": 20, "marginV": 20, "encoding": 1 },
            { "name": "Sign", "fontName": "Arial", "fontSize": 21, "primaryColour": "&H00FFFFFF", "secondaryColour": "&H000000FF", "outlineColour": "&H00292929", "backColour": "&H00000000", "bold": -1, "italic": 0, "underline": 0, "strikeOut": 0, "scaleX": 100, "scaleY": 100, "spacing": 0, "angle": 0, "borderStyle": 1, "outline": 1, "shadow": 0, "alignment": 8, "marginL": 20, "marginR": 20, "marginV": 20, "encoding": 1 }
          ]
        }
        """;

    internal static SubtitleStyleProfile Document { get; } = SubtitleStyleProfile.Parse(Json);
}
