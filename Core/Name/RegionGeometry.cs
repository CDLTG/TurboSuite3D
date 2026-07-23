#nullable disable
using System;
using System.Collections.Generic;

namespace TurboSuite.Name.Regions
{
    /// <summary>
    /// A 2D point/vector in <b>model feet</b> (Revit internal units), Z dropped. The region
    /// pipeline is planar, so the Shim's <c>Autodesk.Revit.DB.XYZ</c> (always Z=0 here) maps to this
    /// Revit-free struct at the Core boundary and back. Exposes exactly the XYZ surface the algorithm
    /// used — component access, +/−, scalar ×/÷, length, normalize, distance — so the ported logic reads
    /// unchanged.
    /// </summary>
    public readonly struct Pt
    {
        public Pt(double x, double y) { X = x; Y = y; }
        public double X { get; }
        public double Y { get; }

        public static Pt operator -(Pt a, Pt b) => new Pt(a.X - b.X, a.Y - b.Y);
        public static Pt operator +(Pt a, Pt b) => new Pt(a.X + b.X, a.Y + b.Y);
        public static Pt operator *(Pt p, double s) => new Pt(p.X * s, p.Y * s);
        public static Pt operator /(Pt p, double s) => new Pt(p.X / s, p.Y / s);

        public double GetLength() => Math.Sqrt(X * X + Y * Y);

        /// <summary>Unit vector; returns this unchanged if it is (near) zero-length. Callers guard length
        /// before calling (matching the old <c>XYZ.Normalize</c> sites, which all length-check first).</summary>
        public Pt Normalize()
        {
            double len = GetLength();
            return len < 1e-12 ? this : new Pt(X / len, Y / len);
        }

        public double DistanceTo(Pt o) => (this - o).GetLength();

        public override string ToString() => $"({X:F3}, {Y:F3})";
    }

    /// <summary>A single wall line segment in model feet. <see cref="IsVirtual"/> is true for
    /// gap-bridge / door-seal segments synthesized by the pipeline (not present in the source CAD).</summary>
    public sealed record WallSeg(Pt Start, Pt End, bool IsVirtual = false);

    /// <summary>One room seed: an interior point (the CAD room-label location) + the room name.</summary>
    public sealed record Seed(Pt Point, string Name);

    /// <summary>
    /// A vectorized room territory: its seed room name + the closed boundary polygon (model feet), plus the
    /// <see cref="Seed"/> point the territory flooded out from.
    /// </summary>
    /// <remarks>
    /// <see cref="Seed"/> is the model point of the pixel that was actually seeded — NOT the raw CAD label
    /// location. When a label lands on a wall the engine spirals to the nearest free pixel, so only the
    /// spiralled pixel is guaranteed to lie inside the territory. Callers use it as a representative interior
    /// point (TurboName's "is this territory already covered by an existing region?" test); a boundary
    /// centroid would not do, because an L-shaped room's centroid can fall outside its own boundary.
    /// </remarks>
    public sealed record GenRegion(string RoomName, List<Pt> Boundary, Pt Seed);

    /// <summary>
    /// The engine's result: the diagnostics report + the vectorized boundaries the caller turns into
    /// FilledRegions, plus the raster grid (+ dimensions + debug markers) so the caller can render the
    /// dev-aid PNG. <see cref="Grid"/> is null when the partition produced no raster (no geometry / too large).
    /// </summary>
    public sealed record WatershedOutput(
        string Report,
        List<GenRegion> Regions,
        int[] Grid,
        int Width,
        int Height,
        List<(int X, int Y, byte R, byte G, byte B)> Markers);
}
