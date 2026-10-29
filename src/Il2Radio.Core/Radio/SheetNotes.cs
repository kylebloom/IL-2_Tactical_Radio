namespace Il2Radio.Core.Radio;

/// <summary>
/// Where a pencil note sits on the kneeboard, so a course and a field name do not share the same ink.
/// Pixels, top-left origin, Y downward, as the sheet is drawn.
/// </summary>
public static class SheetNotes
{
    /// <summary>A patch of ink: a rectangle at any angle. <see cref="Ux"/>, <see cref="Uy"/> is the direction of the words.</summary>
    public readonly record struct Ink(float Cx, float Cy, float Ux, float Uy, float HalfW, float HalfH)
    {
        /// <summary>An upright rectangle whose top-left is (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public static Ink Upright(float x, float y, float w, float h) => new(x + w / 2, y + h / 2, 1, 0, w / 2, h / 2);

        /// <summary>True when the two patches come within <paramref name="gap"/> of each other.</summary>
        public bool Hits(Ink other, float gap)
        {
            return !Separated(this, other, Ux, Uy, gap)
                && !Separated(this, other, -Uy, Ux, gap)
                && !Separated(other, this, other.Ux, other.Uy, gap)
                && !Separated(other, this, -other.Uy, other.Ux, gap);
        }

        private static bool Separated(Ink a, Ink b, float ax, float ay, float gap)
        {
            float dist = MathF.Abs((b.Cx - a.Cx) * ax + (b.Cy - a.Cy) * ay);
            float reach = a.HalfW * MathF.Abs(a.Ux * ax + a.Uy * ay) + a.HalfH * MathF.Abs(-a.Uy * ax + a.Ux * ay)
                + b.HalfW * MathF.Abs(b.Ux * ax + b.Uy * ay) + b.HalfH * MathF.Abs(-b.Uy * ax + b.Ux * ay);
            return dist > reach + gap;
        }
    }

    /// <summary>A course along a leg: where the words start, the rotation, and the ink they occupy.</summary>
    public readonly record struct AlongLeg(float OriginX, float OriginY, float AngleDegrees, float LocalX, float LocalY, Ink Box);

    /// <summary>A name beside a symbol: its top-left, and the ink it occupies.</summary>
    public readonly record struct BesideSymbol(float X, float Y, Ink Box);

    /// <summary>
    /// A course written along the leg from (<paramref name="x1"/>, <paramref name="y1"/>) to (<paramref name="x2"/>, <paramref name="y2"/>),
    /// kept upright and on the left of the way it is flown. Centred on the leg when that clears <paramref name="avoid"/>.
    /// Otherwise it steps out from the line, then onto the other side, then along the leg, and the first clear place is used.
    /// </summary>
    public static AlongLeg Course(float x1, float y1, float x2, float y2, float textW, float textH, float pad, IReadOnlyList<Ink> avoid)
    {
        float dx = x2 - x1, dy = y2 - y1;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f)
            len = 1f;
        float angle = MathF.Atan2(dy, dx) * 180f / MathF.PI;
        bool flipped = angle > 90f || angle < -90f;
        if (angle > 90f)
            angle -= 180f;
        if (angle < -90f)
            angle += 180f;

        double rad = angle * Math.PI / 180.0;
        float ax = (float)Math.Cos(rad), ay = (float)Math.Sin(rad);
        float midX = (x1 + x2) / 2, midY = (y1 + y2) / 2;
        float step = MathF.Max(pad, textH * 0.8f);
        float limit = len / 2 + textW / 2;
        float clear = pad * 2;

        AlongLeg At(float extra, bool left, float shift)
        {
            float gap = pad + extra;
            bool negativeY = left ? !flipped : flipped;
            float localY = negativeY ? -(textH + gap) : gap;
            float ox = midX + shift * ax, oy = midY + shift * ay;
            float ly = localY + textH / 2;
            var box = new Ink(ox + ly * -ay, oy + ly * ax, ax, ay, textW / 2, textH / 2);
            return new AlongLeg(ox, oy, angle, -textW / 2, localY, box);
        }

        AlongLeg? chosen = null;
        foreach (var (extra, left, shift) in Places(pad, step, limit))
        {
            var place = At(extra, left, shift);
            chosen ??= place;
            if (!avoid.Any(ink => place.Box.Hits(ink, clear)))
                return place;
        }
        return chosen ?? At(0, true, 0);
    }

    /// <summary>
    /// A name beside a round symbol. <paramref name="preferredX"/>, <paramref name="preferredY"/> is where it is written when the
    /// paper there is free (to the upper right of a field). Then above, to the left, below, and the corners, and the first
    /// that clears <paramref name="avoid"/> by <paramref name="gap"/>.
    /// </summary>
    public static BesideSymbol Beside(float preferredX, float preferredY, float textW, float textH, float cx, float cy, float radius, float gap, IReadOnlyList<Ink> avoid)
    {
        (float X, float Y)[] spots =
        [
            (preferredX, preferredY),
            (cx - textW / 2, cy - radius - gap - textH),
            (cx - radius - gap - textW, cy - textH * 0.55f),
            (cx - textW / 2, cy + radius + gap),
            (cx + radius + gap, cy - radius - gap - textH),
            (cx - radius - gap - textW, cy - radius - gap - textH),
            (cx + radius + gap, cy + radius + gap),
            (cx - radius - gap - textW, cy + radius + gap),
        ];
        foreach (var (x, y) in spots)
        {
            var box = Ink.Upright(x, y, textW, textH);
            if (!avoid.Any(ink => box.Hits(ink, gap)))
                return new BesideSymbol(x, y, box);
        }
        return new BesideSymbol(preferredX, preferredY, Ink.Upright(preferredX, preferredY, textW, textH));
    }

    /// <summary>Centred on the left first, then further off that side, then the other side, then slid along the leg.</summary>
    private static IEnumerable<(float Extra, bool Left, float Shift)> Places(float pad, float step, float limit)
    {
        float[] extras = [0f, pad * 2, pad * 5, pad * 9, pad * 14];
        foreach (float extra in extras)
            yield return (extra, true, 0);
        foreach (float extra in extras)
            yield return (extra, false, 0);
        for (float s = step; s <= limit; s += step)
        {
            yield return (0, true, s);
            yield return (0, true, -s);
            yield return (0, false, s);
            yield return (0, false, -s);
        }
    }
}
