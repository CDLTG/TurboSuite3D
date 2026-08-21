#nullable enable
using System;

namespace TurboSuite.Dali.Addressing
{
    /// <summary>
    /// A DALI address — form <c>L{loop#}-{shortAddress##}</c> (e.g. <c>L2-00</c> = Loop 2, short address 00).
    /// The <c>##</c> <b>is</b> the DALI hardware short address (0–63), <b>zero-based</b> (the first unit on a
    /// bus is <c>-00</c>) — one per addressable unit (a driver device or a self-driven downlight). Two digits
    /// because a bus holds up to 64 addresses; a third digit is tolerated defensively but never expected.
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
