using System;

namespace TurboSuite.Abstractions
{
    /// <summary>
    /// Revit-free handle to a model element. Decouples Core DTOs/logic from
    /// <c>Autodesk.Revit.DB.ElementId</c> so they can live in the version-agnostic
    /// Core assembly. Per-version shims convert <c>ElementId</c> ⇄ <c>ElementRef</c>
    /// at the boundary (see each shim's ElementId conversion helper).
    ///
    /// The raw id is stored as <see cref="long"/> — Revit 2025+ exposes
    /// <c>ElementId.Value</c> as a long; the 2024 shim widens its int
    /// <c>IntegerValue</c> into the same long at the boundary. Real element ids are
    /// positive; <see cref="None"/> (and the struct default, 0) are treated as "no
    /// element", as is Revit's InvalidElementId (-1) — see <see cref="IsValid"/>.
    /// </summary>
    public readonly struct ElementRef : IEquatable<ElementRef>
    {
        public long Value { get; }

        public ElementRef(long value) => Value = value;

        /// <summary>Sentinel for "no element". Equal to the struct default.</summary>
        public static readonly ElementRef None = new ElementRef(0);

        /// <summary>True for a real element id (positive); false for 0/default and Revit's -1.</summary>
        public bool IsValid => Value > 0;

        public bool Equals(ElementRef other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is ElementRef other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public static bool operator ==(ElementRef a, ElementRef b) => a.Equals(b);
        public static bool operator !=(ElementRef a, ElementRef b) => !a.Equals(b);
        public override string ToString() => Value.ToString();
    }
}
