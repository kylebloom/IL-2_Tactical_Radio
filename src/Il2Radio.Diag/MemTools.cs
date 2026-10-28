using System.Diagnostics;
using System.Globalization;
using Il2Radio.Core.Memory;
using Il2Radio.Windows;

/// <summary>
/// The mem-* commands: read-only tools for finding IL-2's structures (Phase 0 of the memory plan).
/// Each run attaches, does one thing and exits; a scan's results are saved so mem-next can narrow
/// them in a later run, and "rN" names result N in any address.
/// </summary>
static class MemTools
{
    private static string StatePath => Path.Combine(Environment.CurrentDirectory, "diag-output", "mem-scan.bin");

    public static int Run(string command, string[] args)
    {
        var options = new Options(args);
        ProcessMemory? memory;
        try
        {
            memory = ProcessMemory.Open(options.Get("--process") ?? ProcessMemory.Il2ProcessName, options.Int("--pid"));
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }
        if (memory is null)
            return Fail($"{options.Get("--process") ?? ProcessMemory.Il2ProcessName}.exe is not running.");

        using (memory)
        {
            try
            {
                return command switch
                {
                    "mem-info" => Info(memory),
                    "mem-scan" => Scan(memory, options),
                    "mem-next" => Next(memory, options),
                    "mem-near" => Near(memory, options),
                    "mem-ptrs" => Pointers(memory, options),
                    "mem-roster" => Roster(memory, options),
                    "mem-list" => List(memory, options),
                    "mem-dump" => Dump(memory, options),
                    "mem-refs" => Refs(memory, options),
                    "mem-watch" => Watch(memory, options),
                    _ => Fail($"Unknown command {command}"),
                };
            }
            catch (FormatException ex)
            {
                return Fail(ex.Message);
            }
        }
    }

    public const string Help = """
          mem-info                       Attach read-only and show the process, version, modules and memory size
          mem-scan <type> <value|a..b> [--align N] [--module M] [--writable] [--max N]
                                         Search memory. type: i32 i64 f32 f64 ascii utf16. A float matches to the
                                         precision written (1234 is ±0.5); a..b is a range. Saves the results
          mem-next <filter>              Narrow the saved results: changed, unchanged, increased, decreased, or a value/range
          mem-near <type> <value|a..b> [--within N] [--count K]
                                         Keep saved results with at least K (default 1) matching values within N
                                         (hex, default 200) bytes, e.g. an object id with its position nearby
          mem-list [--max N]             Show the saved results with their current values
                                         --fields off:type,... (scan, next, near, list) also shows values at hex offsets
                                         from each hit, e.g. --fields -8:i32,+170:f64
          mem-dump <address> [--bytes N] Show memory as hex, i32, f32, f64, text and pointers (default 256 bytes)
          mem-roster [--id N] [--seconds S] [--interval MS] [--csv file] [--rescan S]
                                         Find every scene object (once per mission) from the player's id in the newest
                                         mission log, and name each from the log; --seconds re-reads them every second;
                                         --rescan S looks for new objects every S s and reports objects that appear or vanish
          mem-compare <csv> [acmi]       Check a mem-roster --csv or positions-*.csv log against the ACMI of the same flight (default: newest);
                                         fits the map between the frames and reports what differs. Needs no game
          mem-ptrs <address> [--count N] [--fields off:type,...]
                                         Walk an array of pointers; --fields reads values inside each object pointed to
          mem-refs <address> [--before N] [--module M] [--max N]
                                         Find pointers to the address or up to N (hex, default 400) bytes before it,
                                         i.e. to a structure holding it. Saves the results like a scan
          mem-watch <addr:type[:label]>... [--seconds S] [--interval MS]
                                         Print values over time (default 60 s, every 500 ms)
                                         Addresses: hex (1A2B or 0x1A2B), module+offset (Il-2.exe+3C0), [x] reads the
                                         pointer at x, rN is saved result N. Example: [[Il-2.exe+1F2A30]+18]+40
                                         --process NAME [--pid N] reads another process (default Il-2). Read-only; needs an
                                         administrator terminal; single-player only
        """;

    private static int Info(ProcessMemory memory)
    {
        var process = memory.Process;
        string version = "";
        try { version = process.MainModule?.FileVersionInfo.FileVersion ?? ""; } catch (System.ComponentModel.Win32Exception) { }
        var regions = memory.Regions;
        ulong total = regions.Aggregate(0UL, (sum, r) => sum + r.Size);
        ulong writable = regions.Where(r => r.Writable).Aggregate(0UL, (sum, r) => sum + r.Size);

        Console.WriteLine($"{process.ProcessName}.exe pid {process.Id}, started {process.StartTime:HH:mm:ss}, file version {version}");
        Console.WriteLine($"Readable memory: {regions.Count} regions, {total / (1 << 20)} MB ({writable / (1 << 20)} MB writable)");
        Console.WriteLine($"Modules: {memory.Modules.Count}");
        foreach (var m in memory.Modules.Take(8))
            Console.WriteLine($"  {m.Name,-28} 0x{m.Base:X12}  {m.Size / 1024,8} KB");
        if (memory.Modules.Count > 8)
            Console.WriteLine($"  ... and {memory.Modules.Count - 8} more");
        Console.WriteLine("Reading only. Use these tools in single-player missions only.");
        return 0;
    }

    private static int Scan(ProcessMemory memory, Options options)
    {
        if (options.Positional(1) is not { } typeText || options.Positional(2) is not { } valueText)
            return Fail("Usage: mem-scan <type> <value|a..b>");
        var type = ScanPattern.ParseType(typeText);
        var pattern = ScanPattern.Parse(type, valueText);
        var scanOptions = new ScanOptions
        {
            Alignment = options.Int("--align"),
            Module = options.Get("--module"),
            WritableOnly = options.Has("--writable"),
            MaxHits = options.Int("--max") ?? 2_000_000,
        };

        var clock = Stopwatch.StartNew();
        var result = MemoryScanner.Scan(memory, pattern, scanOptions);
        Console.WriteLine($"Scanned {result.BytesScanned / (1 << 20)} MB in {result.RegionsScanned} regions in {clock.Elapsed.TotalSeconds:F1} s: "
            + $"{result.Hits.Count} hits{(result.Truncated ? " (stopped at --max)" : "")} for {type} {pattern.Text}");
        Save(memory, type, $"{type} {pattern.Text}", result.Hits);
        PrintHits(memory, type, result.Hits.Select(h => (h, h.Value)).ToList(), options.Int("--max-show") ?? 20, options);
        return 0;
    }

    private static int Next(ProcessMemory memory, Options options)
    {
        if (options.Positional(1) is not { } filterText)
            return Fail("Usage: mem-next <changed|unchanged|increased|decreased|value|a..b>");
        if (LoadFor(memory) is not { } state)
            return 1;
        var filter = RefineFilter.Parse(filterText, state.Type);
        var kept = MemoryScanner.Refine(memory, state.Hits, state.Type, filter);
        Console.WriteLine($"{state.Hits.Count} -> {kept.Count} hits ({filterText}) for {state.Description}");
        Save(memory, state.Type, state.Description, kept);
        PrintHits(memory, state.Type, kept.Select(h => (h, h.Value)).ToList(), options.Int("--max") ?? 20, options);
        return 0;
    }

    private static int Near(ProcessMemory memory, Options options)
    {
        if (options.Positional(1) is not { } typeText || options.Positional(2) is not { } valueText)
            return Fail("Usage: mem-near <type> <value|a..b> [--within N] [--count K]");
        if (LoadFor(memory) is not { } state)
            return 1;
        var type = ScanPattern.ParseType(typeText);
        var pattern = ScanPattern.Parse(type, valueText);
        int within = options.Get("--within") is { } w ? int.Parse(w.Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0x200;
        int minimum = options.Int("--count") ?? 1;

        var kept = MemoryScanner.Near(memory, state.Hits, pattern, within, minimum, options.Int("--align"));
        Console.WriteLine($"{state.Hits.Count} -> {kept.Count} hits with at least {minimum} {type} {pattern.Text} within 0x{within:X} bytes, for {state.Description}");
        if (kept.Count == 0)
        {
            Console.WriteLine("Nothing kept; the saved results are unchanged, so another mem-near can try a different type or range.");
            return 0;
        }
        Save(memory, state.Type, state.Description, kept.Select(k => k.Hit).ToList());

        var book = new AddressBook(memory);
        int max = options.Int("--max") ?? 20;
        for (int i = 0; i < Math.Min(max, kept.Count); i++)
        {
            var (hit, nearby) = kept[i];
            string values = string.Join("  ", nearby.Take(8).Select(n => $"{(n.Offset < 0 ? "-" : "+")}0x{Math.Abs(n.Offset):X} {ScanPattern.Format(type, n.Value)}"));
            Console.WriteLine($"  r{i,-4} {book.Name(hit.Address),-24} {values}{(nearby.Count > 8 ? $"  (+{nearby.Count - 8} more)" : "")}{Fields(memory, options, hit.Address)}");
        }
        if (kept.Count > max)
            Console.WriteLine($"  ... {kept.Count - max} more (--max N)");
        return 0;
    }

    /// <summary>
    /// The once-per-mission discovery (docs/MEMORY.md): the player's object by id, its class pointer, then every
    /// object of that class, named from the mission log. With --seconds, re-reads those objects every second.
    /// </summary>
    private static int Roster(ProcessMemory memory, Options options)
    {
        var names = MissionLogNames(options.Get("--game"), out int? loggedPlayer);
        int? playerId = options.Int("--id") ?? loggedPlayer;
        if (playerId is not { } id)
            return Fail("No player id: pass --id N (the PLID of the ISPL:1 line in the mission log).");

        var clock = Stopwatch.StartNew();
        var player = ObjectRoster.FindPlayer(memory, id, out ulong cls);
        double findPlayer = clock.Elapsed.TotalSeconds;
        if (player is null)
            return Fail($"Player object {id} not found ({findPlayer:F1} s). Is the aircraft in the air yet?");
        var book = new AddressBook(memory);
        Console.WriteLine($"Player {id} at {book.Name(player.Address)}, class {book.Name(cls)} ({findPlayer:F1} s)");

        clock.Restart();
        var objects = ObjectRoster.FindAll(memory, cls);
        Console.WriteLine($"{objects.Count} objects of that class, {objects.Select(o => o.Id).Distinct().Count()} ids ({clock.Elapsed.TotalSeconds:F1} s)");
        PrintRoster(objects, names, id);

        int seconds = options.Int("--seconds") ?? 0;
        var end = DateTime.UtcNow.AddSeconds(seconds);
        // --csv: every object at every read, for comparing with an ACMI afterwards (mem-compare).
        using var csv = options.Get("--csv") is { } csvPath ? new StreamWriter(csvPath) { AutoFlush = true } : null;
        csv?.WriteLine("utc_ms,id,north,height,east,speed");
        int interval = Math.Max(100, options.Int("--interval") ?? 1000);
        // --rescan S: look for new objects (mid-mission spawns) every S seconds. Appearing and vanishing objects
        // are reported as events, with what was last read from a vanished one.
        int rescan = options.Int("--rescan") ?? 0;
        var nextRescan = DateTime.UtcNow.AddSeconds(rescan);
        var nextStatus = DateTime.UtcNow;
        var tracked = objects.ToDictionary(o => o.Address);
        string Name(int objectId) => names.GetValueOrDefault(objectId) ?? "(not in the mission log)";
        while (DateTime.UtcNow < end && !memory.Process.HasExited)
        {
            Thread.Sleep(interval);
            if (rescan > 0 && DateTime.UtcNow >= nextRescan)
            {
                nextRescan = DateTime.UtcNow.AddSeconds(rescan);
                names = MissionLogNames(options.Get("--game"), out _);
                var added = ObjectRoster.FindAll(memory, cls).Where(o => !tracked.ContainsKey(o.Address)).ToList();
                foreach (var o in added)
                    tracked[o.Address] = o;
                foreach (var group in added.GroupBy(o => o.Id))
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  + {group.Key} {Name(group.Key)}: {group.Count()} new object(s) at {group.First().North:F0} {group.First().Height:F0} {group.First().East:F0}, {group.First().Speed * 3.6:F0} km/h");
            }

            clock.Restart();
            var now = new List<MemoryObject>();
            foreach (var (address, before) in tracked.ToList())
            {
                if (ObjectRoster.Read(memory, address, cls) is { } o && o.Id == before.Id)
                {
                    now.Add(o);
                    tracked[address] = o;
                    continue;
                }
                tracked.Remove(address);
                int left = tracked.Values.Count(t => t.Id == before.Id);
                Console.WriteLine($"{DateTime.Now:HH:mm:ss}  - {before.Id} {Name(before.Id)}: object gone ({left} left), last {before.North:F0} {before.Height:F0} {before.East:F0}, {before.Speed * 3.6:F0} km/h");
            }
            long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var o in now)
                csv?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{stamp},{o.Id},{o.North:F3},{o.Height:F3},{o.East:F3},{o.Speed:F3}"));
            if (DateTime.UtcNow < nextStatus)
                continue;
            nextStatus = DateTime.UtcNow.AddSeconds(rescan > 0 ? 10 : 0);
            var me = now.FirstOrDefault(o => o.Id == id);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {now.Count} objects, {now.Select(o => o.Id).Distinct().Count()} ids, read in {clock.Elapsed.TotalMilliseconds:F0} ms"
                + (me is null ? "  player: gone" : $"  player {me.North:F0} {me.Height:F0} {me.East:F0} {me.Speed * 3.6:F0} km/h"));
        }
        return 0;
    }

    private static void PrintRoster(List<MemoryObject> objects, Dictionary<int, string> names, int playerId)
    {
        foreach (var group in objects.GroupBy(o => o.Id).OrderBy(g => names.ContainsKey(g.Key) ? 0 : 1).ThenBy(g => g.Key))
        {
            var o = group.First();
            string name = names.GetValueOrDefault(group.Key) ?? "(not in the mission log)";
            Console.WriteLine($"  {(group.Key == playerId ? "*" : " ")}{group.Key,9} x{group.Count(),-3} {o.North,10:F0} {o.Height,6:F0} {o.East,10:F0}  {o.Speed * 3.6,5:F0} km/h  {name}");
        }
    }

    /// <summary>id → "type | country | name" from the newest mission's text log (AType:12), and the player's PLID (AType:10 ISPL:1).</summary>
    private static Dictionary<int, string> MissionLogNames(string? game, out int? playerId)
    {
        playerId = null;
        var names = new Dictionary<int, string>();
        if (Il2Radio.Core.Il2Install.Locate(game) is not { } install)
            return names;
        var newest = new DirectoryInfo(install.DataDir).EnumerateFiles("missionReport(*)*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest is null)
            return names;
        string stamp = newest.Name[..(newest.Name.IndexOf(')') + 1)];
        var spawn = new System.Text.RegularExpressions.Regex(@"AType:12 ID:(\d+) TYPE:(.+?) COUNTRY:(\d+) NAME:(.*?) PID:");
        var plane = new System.Text.RegularExpressions.Regex(@"AType:10 PLID:(\d+) .*ISPL:1 ");
        foreach (var file in new DirectoryInfo(install.DataDir).EnumerateFiles(stamp.Replace("(", "*").Replace(")", "*") + ".txt"))
        {
            if (!file.Name.StartsWith(stamp, StringComparison.Ordinal))
                continue;
            foreach (string line in File.ReadLines(file.FullName))
            {
                if (spawn.Match(line) is { Success: true } m)
                    names[int.Parse(m.Groups[1].Value)] = $"{m.Groups[2].Value} | {m.Groups[3].Value} | {m.Groups[4].Value}";
                else if (plane.Match(line) is { Success: true } p)
                    playerId = int.Parse(p.Groups[1].Value);
            }
        }
        return names;
    }

    /// <summary>Walks an array of 8-byte pointers and shows --fields read from each object they point to.</summary>
    private static int Pointers(ProcessMemory memory, Options options)
    {
        if (options.Positional(1) is not { } addressText)
            return Fail("Usage: mem-ptrs <address> [--count N] [--fields off:type,...]");
        ulong address = ResolveAddress(memory, addressText);
        int count = Math.Clamp(options.Int("--count") ?? 64, 1, 100_000);
        var data = new byte[count * 8];
        int read = memory.Read(address, data);
        var book = new AddressBook(memory);
        Console.WriteLine($"{read / 8} of {count} entries readable at {book.Name(address)}");
        for (int i = 0; i < read / 8; i++)
        {
            ulong value = BitConverter.ToUInt64(data, i * 8);
            string target = value == 0 ? "null" : book.IsMapped(value) ? book.Name(value) : $"(not a pointer: 0x{value:X})";
            string fields = value != 0 && book.IsMapped(value) ? Fields(memory, options, value) : "";
            Console.WriteLine($"  [{i,5}] +0x{i * 8:X5}  {target,-20}{fields}");
        }
        return 0;
    }

    private static int List(ProcessMemory memory, Options options)
    {
        if (LoadFor(memory) is not { } state)
            return 1;
        Console.WriteLine($"{state.Hits.Count} saved hits for {state.Description}");
        var now = MemoryScanner.ReadAgain(memory, state.Hits).ToDictionary(x => x.Hit.Address, x => x.Now);
        PrintHits(memory, state.Type, state.Hits.Select(h => (h, now.GetValueOrDefault(h.Address) ?? [])).ToList(), options.Int("--max") ?? 50, options);
        return 0;
    }

    private static int Dump(ProcessMemory memory, Options options)
    {
        if (options.Positional(1) is not { } addressText)
            return Fail("Usage: mem-dump <address> [--bytes N]");
        ulong address = ResolveAddress(memory, addressText);
        int length = Math.Clamp(options.Int("--bytes") ?? 256, 8, 1 << 16) / 8 * 8;
        var data = new byte[length];
        int read = memory.Read(address, data);
        var book = new AddressBook(memory);
        Console.WriteLine($"{book.Name(address)} (0x{address:X}), {read} of {length} bytes readable");
        if (read == 0)
            return 1;
        Console.WriteLine($"  off   address       bytes             {"i32",15} {"",11}  {"f32",15} {"",11}  {"f64",18}  text      pointer");
        foreach (string line in MemoryDump.Format(address, data.AsSpan(0, read / 8 * 8), book))
            Console.WriteLine("  " + line);
        return 0;
    }

    private static int Refs(ProcessMemory memory, Options options)
    {
        if (options.Positional(1) is not { } addressText)
            return Fail("Usage: mem-refs <address> [--before N] [--module M]");
        ulong target = ResolveAddress(memory, addressText);
        ulong before = options.Get("--before") is { } b ? ulong.Parse(b.Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0x400;
        var pattern = ScanPattern.PointerTo(target, before);
        var clock = Stopwatch.StartNew();
        var result = MemoryScanner.Scan(memory, pattern, new ScanOptions { Module = options.Get("--module"), MaxHits = options.Int("--max") ?? 100_000 });
        var book = new AddressBook(memory);
        Console.WriteLine($"{result.Hits.Count} pointers to {book.Name(target)} or up to 0x{before:X} before it ({clock.Elapsed.TotalSeconds:F1} s). Static ones (in a module) first:");
        var ordered = result.Hits.OrderBy(h => book.RegionOf(h.Address)?.Module is null).ThenBy(h => h.Address).ToList();
        int index = 0;
        foreach (var hit in ordered.Take(options.Int("--max-show") ?? 40))
        {
            ulong points = BitConverter.ToUInt64(hit.Value);
            Console.WriteLine($"  r{index++,-4} {book.Name(hit.Address),-32} → 0x{points:X}  (target is +0x{target - points:X} into it)");
        }
        Save(memory, ScanType.I64, pattern.Text, ordered);
        return 0;
    }

    private static int Watch(ProcessMemory memory, Options options)
    {
        var fields = options.Positionals().Skip(1).Select(spec => Field.Parse(spec, memory)).ToList();
        if (fields.Count == 0)
            return Fail("Usage: mem-watch <address:type[:label]>...");
        int interval = Math.Max(50, options.Int("--interval") ?? 500);
        var end = DateTime.UtcNow.AddSeconds(options.Int("--seconds") ?? 60);
        Console.WriteLine("time          " + string.Join("  ", fields.Select(f => f.Label.PadLeft(14))));
        while (DateTime.UtcNow < end && !memory.Process.HasExited)
        {
            var values = fields.Select(f =>
            {
                var buffer = new byte[ScanPattern.SizeOf(f.Type)];
                return (memory.Read(f.Address, buffer) == buffer.Length ? ScanPattern.Format(f.Type, buffer) : "?").PadLeft(14);
            });
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {string.Join("  ", values)}");
            Thread.Sleep(interval);
        }
        return 0;
    }

    private sealed record Field(ulong Address, ScanType Type, string Label)
    {
        public static Field Parse(string spec, ProcessMemory memory)
        {
            // The address may itself hold ':'-free brackets and module names, so split from the right.
            var parts = spec.Split(':');
            if (parts.Length < 2)
                throw new FormatException($"\"{spec}\": expected address:type[:label]");
            bool labelled = parts.Length >= 3 && !IsType(parts[^1]);
            string typeText = labelled ? parts[^2] : parts[^1];
            string addressText = string.Join(':', parts[..(labelled ? ^2 : ^1)]);
            var type = ScanPattern.ParseType(typeText);
            if (type is ScanType.Ascii or ScanType.Utf16)
                throw new FormatException("mem-watch reads numbers: i32, i64, f32 or f64");
            return new Field(ResolveAddress(memory, addressText), type, labelled ? parts[^1] : addressText);
        }

        private static bool IsType(string s)
        {
            try { ScanPattern.ParseType(s); return true; } catch (FormatException) { return false; }
        }
    }

    private static void PrintHits(ProcessMemory memory, ScanType type, List<(ScanHit Hit, byte[] Now)> hits, int max, Options options)
    {
        var book = new AddressBook(memory);
        for (int i = 0; i < Math.Min(max, hits.Count); i++)
        {
            var (hit, now) = hits[i];
            string value = now.Length == 0 ? "(unreadable)" : ScanPattern.Format(type, now);
            Console.WriteLine($"  r{i,-4} {book.Name(hit.Address),-32} {value}{Fields(memory, options, hit.Address)}");
        }
        if (hits.Count > max)
            Console.WriteLine($"  ... {hits.Count - max} more (mem-list --max N)");
    }

    /// <summary>
    /// The values --fields asks to show beside each hit: "offset:type" pairs, comma-separated, offsets in hex
    /// relative to the hit ("-8:i32,+170:f64"). Lets one listing show, say, the id and position of every match.
    /// </summary>
    private static string Fields(ProcessMemory memory, Options options, ulong address)
    {
        if (options.Get("--fields") is not { } spec)
            return "";
        var parts = new List<string>();
        foreach (string field in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = field.Split(':');
            if (pieces.Length != 2)
                throw new FormatException($"--fields \"{field}\": expected offset:type, e.g. -8:i32");
            string offsetText = pieces[0].Trim();
            bool negative = offsetText.StartsWith('-');
            string digits = offsetText.TrimStart('+', '-').Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            long offset = long.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture) * (negative ? -1 : 1);
            var type = ScanPattern.ParseType(pieces[1].Trim());
            if (type is ScanType.Ascii or ScanType.Utf16)
                throw new FormatException("--fields shows numbers: i32, i64, f32 or f64");
            var buffer = new byte[ScanPattern.SizeOf(type)];
            bool ok = memory.Read((ulong)((long)address + offset), buffer) == buffer.Length;
            parts.Add($"{offsetText}={(ok ? ScanPattern.Format(type, buffer) : "?")}");
        }
        return "  | " + string.Join("  ", parts);
    }

    private static ulong ResolveAddress(ProcessMemory memory, string text)
    {
        var state = LoadFor(memory, quiet: true);
        return AddressExpression.Evaluate(text, memory, i => state is not null && i >= 0 && i < state.Hits.Count ? state.Hits[i].Address : null);
    }

    private static void Save(ProcessMemory memory, ScanType type, string description, List<ScanHit> hits) =>
        new ScanState(memory.Process.Id, memory.Process.StartTime.ToUniversalTime(), type, description, hits).Save(StatePath);

    private static ScanState? LoadFor(ProcessMemory memory, bool quiet = false)
    {
        var state = ScanState.Load(StatePath);
        string? problem = state is null ? "No saved scan: run mem-scan first."
            : state.ProcessId != memory.Process.Id || Math.Abs((state.ProcessStartUtc - memory.Process.StartTime.ToUniversalTime()).TotalSeconds) > 2
                ? "The saved scan is from an earlier run of the game; its addresses no longer apply. Run mem-scan again."
                : null;
        if (problem is null)
            return state;
        if (!quiet)
            Console.Error.WriteLine(problem);
        return null;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private sealed class Options(string[] args)
    {
        private static readonly string[] Flags = ["--writable"];

        public string? Get(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public int? Int(string name) => int.TryParse(Get(name), out int v) ? v : null;

        public bool Has(string name) => args.Contains(name);

        /// <summary>Arguments that are neither options nor option values. Negative numbers ("-50..50") count as values.</summary>
        public List<string> Positionals() =>
            args.Where((a, i) => !IsOption(a) && (i == 0 || !IsOption(args[i - 1]) || Flags.Contains(args[i - 1]))).ToList();

        public string? Positional(int index) => Positionals() is var p && index < p.Count ? p[index] : null;

        private static bool IsOption(string a) => a.StartsWith("--", StringComparison.Ordinal);
    }
}
