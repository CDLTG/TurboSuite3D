#nullable enable
using System;

namespace TurboSuite.Dali.Addressing
{
    /// <summary>
    /// A DALI design address — form <c>L{loop#}-{load##}</c> (e.g. <c>L2-01</c> = Loop 2, load 01). It is a
    /// design/commissioning LABEL the installer references, <b>not</b> a hardware DALI short address (0–63)
    /// that programs a ballast — a deliberate choice that makes it a string with no 0–63 bookkeeping.
    /// The load is two digits because a bus holds up to 64 loads; a third digit is tolerated defensively but
    /// never expected.
    /// </summary>
    public readonly struct DaliAddress : IEquatable<DaliAddress>
    {
        public DaliAddress(int loopNumber, int loadNumber)
        {
            LoopNumber = loopNumber;
            LoadNumber = loadNumber;
        }

        public int LoopNumber { get; }
        public int LoadNumber { get; }

        /// <summary>The symbolic label written to the "DALI Address" param — <c>L2-01</c>.</summary>
        public string Text => $"L{LoopNumber}-{LoadNumber:D2}";

        public override string ToString() => Text;

        public bool Equals(DaliAddress other) =>
            LoopNumber == other.LoopNumber && LoadNumber == other.LoadNumber;

        public override bool Equals(object? obj) => obj is DaliAddress a && Equals(a);

        public override int GetHashCode() => (LoopNumber * 397) ^ LoadNumber;
    }
}
