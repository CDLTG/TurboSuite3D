#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx.Overlay
{
    /// <summary>A Revit-free RGB color (0–255 per channel). Core can't reference Revit's <c>Color</c> or
    /// <c>System.Windows.Media.Color</c>, so the zone palette emits this and the shim
    /// (<c>Shim/Dmx/DmxZoneColorService</c>) maps it onto a Revit <c>Color</c>.</summary>
    public readonly struct DmxColor
    {
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public DmxColor(byte r, byte g, byte b) { R = r; G = g; B = b; }
    }

    /// <summary>
    /// Pure zone→color assignment for the Control-Zone view overlay. The colors
    /// exist purely to TELL ZONES APART in the active view while the TurboDMX window is open — exact hues
    /// don't matter, but the mapping must be DETERMINISTIC and stable across window opens so a given zone
    /// keeps its color. Achieved by sorting the distinct zone names and walking the golden-angle (≈137.5°)
    /// hue rotation, which keeps successive indices visually far apart for any zone count. The Revit-side
    /// filter/override work lives in <c>Shim/Dmx/Services/DmxZoneColorService</c>.
    /// </summary>
    public static class DmxZonePalette
    {
        private const double GoldenAngle = 137.50776405003785; // 360 × (1 − 1/φ)
        private const double Saturation = 1.0;                 // full — the families show only a symbol (no
                                                               // geometry to read through), so colors go bold
        private const double Brightness = 1.0;

        /// <summary>Map each non-blank distinct zone name to a stable color, keyed off its sorted position.</summary>
        public static IReadOnlyDictionary<string, DmxColor> Build(IEnumerable<string?>? zoneNames)
        {
            var sorted = (zoneNames ?? Enumerable.Empty<string?>())
                .Where(z => !string.IsNullOrWhiteSpace(z))
                .Select(z => z!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(z => z, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var map = new Dictionary<string, DmxColor>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sorted.Count; i++)
            {
                double hue = (i * GoldenAngle) % 360.0;
                map[sorted[i]] = FromHsv(hue, Saturation, Brightness);
            }
            return map;
        }

        private static DmxColor FromHsv(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double r, g, b;
            if (h < 60)       { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else              { r = c; g = 0; b = x; }
            return new DmxColor(To255(r + m), To255(g + m), To255(b + m));
        }

        private static byte To255(double v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * 255)));
    }
}
