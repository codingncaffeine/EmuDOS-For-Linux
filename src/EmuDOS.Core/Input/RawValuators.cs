namespace EmuDOS.Core.Input;

/// <summary>
/// Decodes an XInput2 valuator set (a bit mask plus the packed values of the set bits) into a
/// relative X/Y mouse delta. Valuators 0 and 1 are the pointer's relative X and Y axes on every
/// mouse; higher valuators are scroll wheels and the like, which a DOS game never sees.
/// Platform-neutral so the decoding is unit-testable away from a live X connection.
/// </summary>
public static class RawValuators
{
    /// <summary>
    /// Extract the X (valuator 0) and Y (valuator 1) deltas. <paramref name="values"/> holds one
    /// double per SET bit of <paramref name="mask"/>, in ascending valuator order — the XI2 packing.
    /// Unset axes read as 0. Returns false when neither axis is present.
    /// </summary>
    public static bool TryExtractXY(ReadOnlySpan<byte> mask, ReadOnlySpan<double> values, out double dx, out double dy)
    {
        dx = 0;
        dy = 0;
        bool any = false;
        int next = 0; // index into values of the next set bit
        int bits = mask.Length * 8;
        for (int v = 0; v < bits; v++)
        {
            if ((mask[v >> 3] & (1 << (v & 7))) == 0)
                continue;
            if (next >= values.Length)
                break; // malformed: more set bits than values
            double value = values[next++];
            if (v == 0) { dx = value; any = true; }
            else if (v == 1) { dy = value; any = true; }
            else if (v > 1) break; // past the two pointer axes — nothing else matters
        }
        return any;
    }
}
