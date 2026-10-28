using System.Globalization;
using System.IO.Compression;
using Il2Radio.Core.Acmi;
using Il2Radio.Core.Geometry;

/// <summary>
/// mem-compare: checks positions read from memory (mem-roster --csv) against the ACMI track of the same flight.
/// The two use different frames (game metres vs latitude/longitude, and the only conversion we have is a
/// four-town fit good to ~360 m), so the best affine map between them is fitted from the data itself and
/// what remains is reported. Matching is by nearest point on each aircraft's track, so no clock is needed;
/// the matched track times then show whether memory runs in step with the track.
/// </summary>
static class MemCompare
{
    private sealed record Sample(long Utc, double North, double Height, double East);
    private sealed record TrackPoint(double Time, double North, double East, double Altitude);

    public static int Run(string? csvPath, string? acmiPath)
    {
        if (csvPath is null || !File.Exists(csvPath))
            return Fail("Usage: mem-compare <memory.csv> [track.acmi]  (from mem-roster --csv, or the radio's positions-*.csv)");
        if (acmiPath is null || !File.Exists(acmiPath))
            return Fail("ACMI file not found.");

        // Only memory samples taken while the track was recording: from the file's creation to its last write
        // (IL-2 writes it until recording stops). Samples outside would match the track's ends and skew the fit.
        var info = new FileInfo(acmiPath);
        long from = new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeMilliseconds(), until = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        var memory = LoadCsv(csvPath, from, until);
        Console.WriteLine($"Track recorded {info.CreationTime:HH:mm:ss}-{info.LastWriteTime:HH:mm:ss}; memory samples outside that are left out.");
        var tracks = LoadTracks(acmiPath);
        Console.WriteLine($"Memory: {memory.Count} aircraft/objects, {memory.Values.Sum(s => s.Count)} moving samples. ACMI: {tracks.Count} aircraft tracks.");

        var ids = memory.Keys.Where(tracks.ContainsKey).ToList();
        if (ids.Count == 0)
            return Fail("No id is in both (memory ids are mission-log ids; ACMI ids are the same numbers in hex).");

        // Start from the rough four-town projection, then refine an affine map from nearest-point pairs.
        var map = Affine.Identity;
        List<(int Id, Sample Mem, TrackPoint Track, double Dist)> pairs = [];
        for (int pass = 0; pass < 4; pass++)
        {
            pairs = ids.SelectMany(id => memory[id].Select(s => Nearest(id, s, tracks[id], map))).ToList();
            var good = pairs.OrderBy(p => p.Dist).Take(Math.Max(10, pairs.Count * 9 / 10)).ToList();
            map = Affine.Fit(good.Select(p => (p.Track.North, p.Track.East, p.Mem.North, p.Mem.East)).ToList());
        }
        pairs = ids.SelectMany(id => memory[id].Select(s => Nearest(id, s, tracks[id], map))).ToList();

        Console.WriteLine($"Fitted map from the projected track to memory: {map}");
        Console.WriteLine($"{"id",9} {"samples",7} {"horiz median",13} {"95%",8} {"max",8} {"height diff median",19} {"95%",8}  track time vs wall clock");
        foreach (var group in pairs.GroupBy(p => p.Id).OrderBy(g => g.Key))
        {
            var d = group.Select(p => p.Dist).Order().ToList();
            var h = group.Select(p => Math.Abs(p.Mem.Height - p.Track.Altitude)).Order().ToList();
            var signedH = group.Select(p => p.Mem.Height - p.Track.Altitude).Order().ToList();
            Console.WriteLine($"{group.Key,9} {group.Count(),7} {Pct(d, 50),11:F1} m {Pct(d, 95),6:F1} m {d[^1],6:F1} m {Pct(signedH, 50),17:F1} m {Pct(h, 95),6:F1} m  {Clock(group.ToList())}");
        }
        var all = pairs.Select(p => p.Dist).Order().ToList();
        var allH = pairs.Select(p => Math.Abs(p.Mem.Height - p.Track.Altitude)).Order().ToList();
        Console.WriteLine($"All: {pairs.Count} samples, horizontal median {Pct(all, 50):F1} m, 95% {Pct(all, 95):F1} m; height |diff| median {Pct(allH, 50):F1} m, 95% {Pct(allH, 95):F1} m");
        return 0;
    }

    /// <summary>Slope and scatter of matched track time against wall-clock time: 1.0 and small scatter means in step.</summary>
    private static string Clock(List<(int Id, Sample Mem, TrackPoint Track, double Dist)> pairs)
    {
        if (pairs.Count < 5)
            return "";
        double mx = pairs.Average(p => p.Mem.Utc / 1000.0), my = pairs.Average(p => p.Track.Time);
        double sxx = pairs.Sum(p => Math.Pow(p.Mem.Utc / 1000.0 - mx, 2)), sxy = pairs.Sum(p => (p.Mem.Utc / 1000.0 - mx) * (p.Track.Time - my));
        if (sxx <= 0)
            return "";
        double slope = sxy / sxx;
        var residuals = pairs.Select(p => Math.Abs(my + slope * (p.Mem.Utc / 1000.0 - mx) - p.Track.Time)).Order().ToList();
        return $"slope {slope:F3}, scatter median {Pct(residuals, 50):F2} s";
    }

    private static (int, Sample, TrackPoint, double) Nearest(int id, Sample s, List<TrackPoint> track, Affine map)
    {
        TrackPoint best = track[0];
        double bestD = double.MaxValue;
        for (int i = 0; i < track.Count; i++)
        {
            var (n, e) = map.Apply(track[i].North, track[i].East);
            double d = Math.Sqrt((n - s.North) * (n - s.North) + (e - s.East) * (e - s.East));
            if (d < bestD)
            {
                bestD = d;
                best = track[i];
            }
            if (i + 1 < track.Count)
            {
                // Nearest point on the segment to the next sample, so ~5 Hz track samples don't add their spacing.
                var (n2, e2) = map.Apply(track[i + 1].North, track[i + 1].East);
                double dn = n2 - n, de = e2 - e, len2 = dn * dn + de * de;
                if (len2 > 0)
                {
                    double t = Math.Clamp(((s.North - n) * dn + (s.East - e) * de) / len2, 0, 1);
                    double pn = n + t * dn, pe = e + t * de;
                    double ds = Math.Sqrt((pn - s.North) * (pn - s.North) + (pe - s.East) * (pe - s.East));
                    if (ds < bestD)
                    {
                        bestD = ds;
                        var a = track[i];
                        var b = track[i + 1];
                        best = new TrackPoint(a.Time + t * (b.Time - a.Time), a.North + t * (b.North - a.North), a.East + t * (b.East - a.East), a.Altitude + t * (b.Altitude - a.Altitude));
                    }
                }
            }
        }
        return (id, s, best, bestD);
    }

    private static Dictionary<int, List<Sample>> LoadCsv(string path, long from, long until)
    {
        var byId = new Dictionary<int, List<Sample>>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split(',');
            if (f.Length < 6)
                continue;
            var s = new Sample(long.Parse(f[0], CultureInfo.InvariantCulture), Double(f[2]), Double(f[3]), Double(f[4]));
            if (s.Utc < from || s.Utc > until)
                continue;
            int id = int.Parse(f[1], CultureInfo.InvariantCulture);
            var list = byId.TryGetValue(id, out var l) ? l : byId[id] = [];
            // Repeats are the game paused (out of focus) or a parked object: they say nothing about accuracy.
            if (list.Count > 0 && list[^1].North == s.North && list[^1].East == s.East)
                continue;
            list.Add(s);
        }
        return byId.Where(kv => kv.Value.Count >= 3).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static Dictionary<int, List<TrackPoint>> LoadTracks(string path)
    {
        string text;
        using (var file = File.OpenRead(path))
        {
            Stream content = file;
            ZipArchive? zip = null;
            if (file.ReadByte() == 'P')
            {
                file.Position = 0;
                zip = new ZipArchive(file, ZipArchiveMode.Read);
                content = zip.Entries[0].Open();
            }
            else
            {
                file.Position = 0;
            }
            using var reader = new StreamReader(content);
            text = reader.ReadToEnd() + "\n";
            zip?.Dispose();
        }

        var tracks = new Dictionary<int, List<TrackPoint>>();
        var acmi = new AcmiStream();
        var recorded = new Dictionary<string, double>();
        void Record(AcmiStream s)
        {
            foreach (var o in s.Objects.Values)
            {
                // Each position once: only when this object's position time has moved on since the last frame.
                if (!o.IsAircraft || !o.HasPosition || o.Removed || double.IsNaN(o.Altitude)
                    || (recorded.TryGetValue(o.Id, out double last) && last == o.LastPositionTime))
                    continue;
                recorded[o.Id] = o.LastPositionTime;
                if (!int.TryParse(o.Id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int id))
                    continue;
                var (n, e) = MapGrid.ProjectStalingrad(s.ReferenceLatitude + o.Latitude, s.ReferenceLongitude + o.Longitude);
                var list = tracks.TryGetValue(id, out var l) ? l : tracks[id] = [];
                list.Add(new TrackPoint(o.LastPositionTime, n, e, o.Altitude));
            }
        }
        acmi.FrameClosed = Record;
        acmi.Feed(text);
        Record(acmi);
        return tracks.Where(kv => kv.Value.Count >= 2).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static double Pct(List<double> sorted, int percent) =>
        sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * percent / 100.0) - 1)];

    private static double Double(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    /// <summary>north' = a·n + b·e + c, east' = d·n + e·e + f.</summary>
    private sealed record Affine(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Affine Identity = new(1, 0, 0, 0, 1, 0);

        public (double North, double East) Apply(double n, double e) => (A * n + B * e + C, D * n + E * e + F);

        public static Affine Fit(List<(double N, double E, double TargetN, double TargetE)> pairs)
        {
            // Normal equations for [n e 1] · coefficients = target, solved for each output.
            double[,] m = new double[3, 3];
            double[] vn = new double[3], ve = new double[3];
            foreach (var p in pairs)
            {
                double[] x = [p.N, p.E, 1];
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++)
                        m[i, j] += x[i] * x[j];
                    vn[i] += x[i] * p.TargetN;
                    ve[i] += x[i] * p.TargetE;
                }
            }
            var cn = Solve(m, vn);
            var ce = Solve(m, ve);
            return cn is null || ce is null ? Identity : new Affine(cn[0], cn[1], cn[2], ce[0], ce[1], ce[2]);
        }

        private static double[]? Solve(double[,] m, double[] v)
        {
            var a = (double[,])m.Clone();
            var b = (double[])v.Clone();
            for (int col = 0; col < 3; col++)
            {
                int pivot = Enumerable.Range(col, 3 - col).MaxBy(r => Math.Abs(a[r, col]));
                if (Math.Abs(a[pivot, col]) < 1e-12)
                    return null;
                for (int k = 0; k < 3; k++)
                    (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
                for (int r = 0; r < 3; r++)
                {
                    if (r == col)
                        continue;
                    double f = a[r, col] / a[col, col];
                    for (int k = 0; k < 3; k++)
                        a[r, k] -= f * a[col, k];
                    b[r] -= f * b[col];
                }
            }
            return [b[0] / a[0, 0], b[1] / a[1, 1], b[2] / a[2, 2]];
        }

        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"n' = {A:F5}n + {B:F5}e + {C:F1}, e' = {D:F5}n + {E:F5}e + {F:F1}");
    }
}
