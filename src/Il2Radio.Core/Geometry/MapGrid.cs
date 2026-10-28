namespace Il2Radio.Core.Geometry;

/// <summary>
/// One square on the IL-2 map grid. <see cref="Row"/> counts from the north edge and <see cref="Column"/>
/// from the west, both starting at 1, so the text matches the four digits printed on the map ("1022").
/// <see cref="Ninth"/> is the keypad cell inside that square, 1 in its southwest corner through 9 in its
/// northeast, and is null when the fix is too close to a ninth-line to name one.
/// </summary>
public readonly record struct GridSquare(int Row, int Column, int? Ninth)
{
    public string Text => Ninth is int n ? $"{Row:00}{Column:00}-{n}" : $"{Row:00}{Column:00}";

    /// <summary>Row and column packed so 10 and 22 become 1022. Columns stay under 100 on every Great Battles map.</summary>
    public int Code => Row * 100 + Column;
}

/// <summary>
/// The 10 km squares IL-2 draws on the mission map, and the nine 3.3 km cells inside each one.
/// Labels were read off the Stalingrad map on 2026-10-26: southwest 2301-1, northwest 0101-7,
/// northeast 0136-9, southeast 2336-3. Karpovka, Kalach, Basargino and Verbovka then fell in the
/// squares their mission-file positions predict.
/// </summary>
public static class MapGrid
{
    public const double SquareMetres = 10_000;

    /// <summary>Closer than this to a 10 km line, the square itself is left unsaid. The lat/lon fit is a few hundred metres.</summary>
    public const double SquareMarginMetres = 1_000;

    /// <summary>Closer than this to a line inside the square, the ninth is left unsaid and the four digits remain.</summary>
    public const double NinthMarginMetres = 500;

    /// <summary>Stalingrad's labeled rows: 01 at the northern edge, 23 at the southern edge.</summary>
    public const int StalingradNorthRows = 23;

    /// <summary>Stalingrad's labeled columns: 01 at the western edge, 36 at the eastern edge.</summary>
    public const int StalingradEastColumns = 36;

    // Linear fit of ACMI latitude/longitude to mission-file metres, four airfields whose ACMI altitude
    // matched the mission object (Verbovka, Karpovka, Kalach, Tusov; 2026-10-26). Largest residual 360 m.
    // The fit keeps each of those four in the same cell as the mission position. Tusov is under a
    // kilometre from a grid line, so a reply omits his square.
    private const double StalingradLatOrigin = 47.4593465892835;
    private const double StalingradLonOrigin = 41.1130082026225;
    private const double StalingradMetresPerDegreeLat = 107254.374215108;
    private const double StalingradMetresPerDegreeLon = 73567.0739069957;

    public static bool IsStalingrad(string guiMap) =>
        guiMap.Contains("stalingrad", StringComparison.OrdinalIgnoreCase);

    /// <summary>Mission-file north/east (X/Z, metres) for a Stalingrad latitude and longitude, as ACMI writes them.</summary>
    public static (double North, double East) ProjectStalingrad(double latitude, double longitude) =>
        ((latitude - StalingradLatOrigin) * StalingradMetresPerDegreeLat,
         (longitude - StalingradLonOrigin) * StalingradMetresPerDegreeLon);

    /// <summary>Inverse of <see cref="ProjectStalingrad"/>.</summary>
    public static (double Latitude, double Longitude) StalingradLatLon(double north, double east) =>
        (north / StalingradMetresPerDegreeLat + StalingradLatOrigin,
         east / StalingradMetresPerDegreeLon + StalingradLonOrigin);

    /// <summary>
    /// The square containing a mission-file position, or null when it falls outside the labeled grid
    /// or closer than <see cref="SquareMarginMetres"/> to a 10 km line.
    /// </summary>
    public static GridSquare? TryLabel(double northMetres, double eastMetres, int northRows = StalingradNorthRows, int eastColumns = StalingradEastColumns)
    {
        if (northRows <= 0 || eastColumns <= 0 || eastColumns >= 100)
            return null;

        int northBand = (int)Math.Floor(northMetres / SquareMetres);
        int eastBand = (int)Math.Floor(eastMetres / SquareMetres);
        int row = northRows - northBand;
        int column = eastBand + 1;
        if (row < 1 || row > northRows || column < 1 || column > eastColumns)
            return null;

        double localNorth = northMetres - northBand * SquareMetres;
        double localEast = eastMetres - eastBand * SquareMetres;
        double squareClearance = Math.Min(Math.Min(localNorth, SquareMetres - localNorth), Math.Min(localEast, SquareMetres - localEast));
        if (squareClearance < SquareMarginMetres)
            return null;

        double third = SquareMetres / 3.0;
        int subRow = Math.Min(2, (int)Math.Floor(localNorth / third));
        int subColumn = Math.Min(2, (int)Math.Floor(localEast / third));
        double northInto = localNorth - subRow * third;
        double eastInto = localEast - subColumn * third;
        double ninthClearance = Math.Min(Math.Min(northInto, third - northInto), Math.Min(eastInto, third - eastInto));
        int? ninth = ninthClearance < NinthMarginMetres ? null : subRow * 3 + subColumn + 1;
        return new GridSquare(row, column, ninth);
    }

    /// <summary>
    /// The square (and, with <paramref name="ninth"/>, its keypad cell) a position lies in, with no margin at the
    /// lines: for a pilot matching the ground under him to the sheet, who sees which side of a line he is on.
    /// Null outside the labeled grid.
    /// </summary>
    public static GridSquare? Containing(double northMetres, double eastMetres, bool ninth, int northRows = StalingradNorthRows, int eastColumns = StalingradEastColumns)
    {
        int northBand = (int)Math.Floor(northMetres / SquareMetres);
        int eastBand = (int)Math.Floor(eastMetres / SquareMetres);
        int row = northRows - northBand, column = eastBand + 1;
        if (row < 1 || row > northRows || column < 1 || column > eastColumns || eastColumns >= 100)
            return null;
        if (!ninth)
            return new GridSquare(row, column, null);
        double third = SquareMetres / 3.0;
        int subRow = Math.Clamp((int)Math.Floor((northMetres - northBand * SquareMetres) / third), 0, 2);
        int subColumn = Math.Clamp((int)Math.Floor((eastMetres - eastBand * SquareMetres) / third), 0, 2);
        return new GridSquare(row, column, subRow * 3 + subColumn + 1);
    }

    /// <summary>
    /// The area a square names, in mission metres: the whole 10 km square, or its ninth when the square has one.
    /// The inverse of <see cref="TryLabel"/> and <see cref="Containing"/>.
    /// </summary>
    public static (double South, double West, double Size) Bounds(GridSquare square, int northRows = StalingradNorthRows)
    {
        double south = (northRows - square.Row) * SquareMetres;
        double west = (square.Column - 1) * SquareMetres;
        if (square.Ninth is not int n)
            return (south, west, SquareMetres);
        double third = SquareMetres / 3.0;
        return (south + (n - 1) / 3 * third, west + (n - 1) % 3 * third, third);
    }

    /// <summary>The square named by <see cref="GridSquare.Code"/> and an optional ninth ("1"–"9"), as a reply's grid token carries them.</summary>
    public static GridSquare FromCode(int code, string? ninth) =>
        new(code / 100, code % 100, int.TryParse(ninth, out int n) && n is >= 1 and <= 9 ? n : null);

    /// <summary>The four digits printed on the map for the square whose south-west corner is at these mission metres ("1120").</summary>
    public static string? SquareLabel(double southMetres, double westMetres, int northRows = StalingradNorthRows, int eastColumns = StalingradEastColumns) =>
        Containing(southMetres + 1, westMetres + 1, ninth: false, northRows, eastColumns)?.Text;
}
