using System.Collections.Generic;

namespace TurboSuite.Shared.Helpers;

/// <summary>
/// Compares strings by splitting them into alternating non-digit and digit runs,
/// so "A2" sorts before "A10". Letter runs are compared case-insensitively;
/// digit runs are compared by numeric value (with leading-zero count as a tiebreak).
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer OrdinalIgnoreCase = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            bool xDigit = char.IsDigit(x[i]);
            bool yDigit = char.IsDigit(y[j]);

            if (xDigit && yDigit)
            {
                int xStart = i;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                int yStart = j;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                int xLen = i - xStart;
                int yLen = j - yStart;

                int xFirstNonZero = xStart;
                while (xFirstNonZero < i - 1 && x[xFirstNonZero] == '0') xFirstNonZero++;
                int yFirstNonZero = yStart;
                while (yFirstNonZero < j - 1 && y[yFirstNonZero] == '0') yFirstNonZero++;

                int xDigits = i - xFirstNonZero;
                int yDigits = j - yFirstNonZero;
                if (xDigits != yDigits) return xDigits - yDigits;

                for (int k = 0; k < xDigits; k++)
                {
                    int cmp = x[xFirstNonZero + k] - y[yFirstNonZero + k];
                    if (cmp != 0) return cmp;
                }

                if (xLen != yLen) return xLen - yLen;
            }
            else
            {
                char cx = char.ToUpperInvariant(x[i]);
                char cy = char.ToUpperInvariant(y[j]);
                if (cx != cy) return cx - cy;
                i++;
                j++;
            }
        }

        return (x.Length - i) - (y.Length - j);
    }
}
