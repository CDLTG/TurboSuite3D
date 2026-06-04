// Compile-time polyfill for the C# index-from-end (^) and range (..) operators.
//
// `expr[^1]` and `expr[a..b]` lower to System.Index / System.Range, which ship in
// .NET Core 3.0+ / .NET 5+ but do NOT exist in netstandard2.0 or .NET Framework 4.8.
// Declaring them here lets TurboSuite.Core (and the future net48 Revit2024 shim)
// compile migrated logic that uses these operators.
//
// Guarded so it vanishes on net8 (where the real public types exist and would
// otherwise collide) once Core multi-targets net48;net8.0-windows. Implementation
// mirrors the reference structs in the .NET runtime.
#if !NET5_0_OR_GREATER
namespace System
{
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;

        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Non-negative number required.");
            _value = fromEnd ? ~value : value;
        }

        private Index(int value) => _value = value;

        public static Index Start => new Index(0);
        public static Index End => new Index(~0);

        public static Index FromStart(int value)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Non-negative number required.");
            return new Index(value);
        }

        public static Index FromEnd(int value)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Non-negative number required.");
            return new Index(~value);
        }

        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;

        public int GetOffset(int length) => IsFromEnd ? length - (~_value) : _value;

        public override bool Equals(object? value) => value is Index other && _value == other._value;
        public bool Equals(Index other) => _value == other._value;
        public override int GetHashCode() => _value;

        public static implicit operator Index(int value) => FromStart(value);
    }

    internal readonly struct Range : IEquatable<Range>
    {
        public Index Start { get; }
        public Index End { get; }

        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        public override bool Equals(object? value) => value is Range r && r.Start.Equals(Start) && r.End.Equals(End);
        public bool Equals(Range other) => other.Start.Equals(Start) && other.End.Equals(End);
        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();

        public static Range StartAt(Index start) => new Range(start, Index.End);
        public static Range EndAt(Index end) => new Range(Index.Start, end);
        public static Range All => new Range(Index.Start, Index.End);

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
                throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }
    }
}
#endif
