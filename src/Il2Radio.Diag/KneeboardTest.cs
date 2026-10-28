using Il2Radio.Core.Contacts;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.Memory;
using Il2Radio.Core.MissionFile;
using Il2Radio.Core.MissionLog;
using Il2Radio.Core.Radio;
using Il2Radio.Windows;

/// <summary>
/// Diag kneeboard-test: the kneeboard of a real Career mission, drawn without IL-2. The player's flight from the mission
/// file is put on its second briefed leg in memory positions, the leader beside the player; the radio is asked where they
/// are on the route, the flight flies on four minutes by dead reckoning, then the flight and the contacts are asked for,
/// with a pair of enemy fighters 2.5 km off the nose. Every mark comes from the replies, exactly as in a flight. Writes
/// the small and the large view as PNGs.
/// </summary>
static class KneeboardTest
{
    public static int Run(string? missionPath, string outputDir, int screenHeight)
    {
        if (missionPath is null || !File.Exists(missionPath))
        {
            Console.Error.WriteLine("No mission file. Pass one, or fly a Career mission so data\\Missions\\_gen.Mission exists.");
            return 1;
        }
        var plan = MissionPlan.Load(missionPath);
        if (plan.Player is not { } me)
        {
            Console.Error.WriteLine("The mission has no player.");
            return 1;
        }
        var flight = plan.Aircraft.Where(a => a.Country == me.Country && a.Callsign == me.Callsign && me.Callsign > 0).OrderBy(a => a.Callnum).ToList();
        if (flight.Count == 0)
            flight = [me];
        if (flight.Select(a => plan.BriefedRoutes.GetValueOrDefault(a.Index)).FirstOrDefault(r => r is { Count: >= 3 }) is not { } route)
        {
            Console.Error.WriteLine("The player's flight has no briefed route of three waypoints (an escort?). Pass a sweep or patrol mission.");
            return 1;
        }

        var tracker = new ContactTracker();
        tracker.SetPlan(plan);
        int id = 1_000_000;
        var ids = new Dictionary<PlannedAircraft, int>();
        foreach (var a in flight)
        {
            ids[a] = id;
            var origin = a.StartPosition;
            tracker.Apply(new ObjectSpawnedEvent(0, id, "Bf 109 F-4", a.Country, a.PilotName, -1, origin));
            tracker.Apply(new ObjectSpawnedEvent(0, id + 1, "BotPilot", a.Country, "BotPilot", id, origin));
            tracker.Apply(new PlaneSpawnedEvent(0, id, id + 1, origin, "Bf 109 F-4", a.Country, a.Callnum, 1, 1, -1, a.IsPlayer, ""));
            id += 16;
        }
        int enemy = id, wingman = id + 16;
        int enemyCountry = plan.CountryToCoalition.Keys.FirstOrDefault(c => plan.CoalitionOf(c) != plan.CoalitionOf(me.Country) && plan.CoalitionOf(c) > 0);
        foreach (int e in new[] { enemy, wingman })
        {
            tracker.Apply(new ObjectSpawnedEvent(0, e, "Hurricane Mk.II", enemyCountry, "Pilot", -1, route[0].Position));
            tracker.Apply(new ObjectSpawnedEvent(0, e + 1, "BotPilot", enemyCountry, "BotPilot", e, route[0].Position));
        }

        var kneeboard = new Kneeboard();
        kneeboard.Reset(plan);
        var settings = new RadioSettings { Realism = RadioRealism.Pilot };
        var memory = new SightingMemory();
        var start = DateTime.UtcNow.AddMinutes(-6);
        var leg = (From: route[1].Position, To: route[2].Position);
        double course = Navigation.Relative(leg.From, leg.To)!.Value.BearingDegrees;
        double c = course * Math.PI / 180;
        const double speed = 110; // m/s, about 400 km/h

        (double N, double E) Along(double metres) => (leg.From.North + 0.25 * (leg.To.North - leg.From.North) + metres * Math.Cos(c),
                                                      leg.From.East + 0.25 * (leg.To.East - leg.From.East) + metres * Math.Sin(c));
        MemoryAircraft Plane(PlannedAircraft a, (double N, double E) at, double right, double back) =>
            new(ids[a], at.N - back * Math.Cos(c) + right * Math.Cos(c + Math.PI / 2), 2500, at.E - back * Math.Sin(c) + right * Math.Sin(c + Math.PI / 2), speed);

        void Fly(double seconds, double at, bool enemies)
        {
            var here = Along(at);
            var read = flight.Select((a, i) => a.IsPlayer ? Plane(a, here, 0, 0)
                : i == 0 ? Plane(a, here, 300, 100)
                : Plane(a, here, -4_000 * i, 3_000)).ToList(); // the others a few kilometres off the left wing
            if (enemies)
            {
                read.Add(new MemoryAircraft(enemy, here.N + 2_500 * Math.Cos(c + 0.5), 2_900, here.E + 2_500 * Math.Sin(c + 0.5), 120));
                read.Add(new MemoryAircraft(wingman, here.N + 2_600 * Math.Cos(c + 0.55), 2_900, here.E + 2_600 * Math.Sin(c + 0.55), 120));
            }
            // Two reads a second apart, so every aircraft has a course over ground.
            tracker.Apply(read.Select(r => r with { North = r.North - 30 * Math.Cos(c), East = r.East - 30 * Math.Sin(c) }).ToList(), seconds - 1);
            tracker.Apply(read, seconds);
            EnemyCommands.Notice(tracker, settings, memory);
        }

        void Ask(RadioCommand command, DateTime utc)
        {
            var result = RadioCommands.Execute(command, tracker, settings, memory);
            Console.WriteLine($"{utc - start:mm\\:ss}  {RadioCommands.Title(command)} → {result.Outcome}");
            foreach (var reply in result.Replies)
            {
                Console.WriteLine($"         \"{reply.Text}\"");
                kneeboard.Record(command, reply, tracker.Player?.LastFix?.Heading, utc, (plan.Weather.StartTime + (utc - start)).ToString(@"hh\:mm"));
            }
        }

        Fly(10, 0, enemies: false);
        Ask(RadioCommand.RoutePosition, start);
        Ask(RadioCommand.FlightPosition, start.AddSeconds(20));
        for (int s = 1; s <= 240; s++)
            kneeboard.Advance(start.AddSeconds(s), course, speed);
        Fly(250, 240 * speed, enemies: true);
        var now = start.AddSeconds(240);
        Ask(RadioCommand.FlightPosition, now);
        Ask(RadioCommand.EnemyContacts, now.AddSeconds(5));
        Ask(RadioCommand.SteerHome, now.AddSeconds(10));
        var later = now.AddSeconds(70);

        Directory.CreateDirectory(outputDir);
        foreach (var view in new[] { KneeboardView.Small, KneeboardView.Large })
        {
            var picture = kneeboard.Picture(later, view)!;
            string path = Path.Combine(outputDir, $"kneeboard-{view.ToString().ToLowerInvariant()}.png");
            KneeboardPanel.Save(picture, screenHeight, path);
            Console.WriteLine($"wrote {path}");
            if (view == KneeboardView.Large)
            {
                foreach (var m in picture.Marks)
                    Console.WriteLine($"  mark {m.Mark.Subject} {m.Mark.Label} {m.Mark.Shape} {m.Mark.Size / 1000:F1} km, {m.AgeText}, alpha {m.Alpha:F2}");
                if (picture.Player is { } p)
                    Console.WriteLine($"  you: dead reckoning, {p.Mark.Size / 1000:F1} km radius; {(picture.OffSheet is { } edge ? "off the sheet " + edge : "on the sheet")}");
                foreach (string line in picture.Footer)
                    Console.WriteLine($"  footer: {line}");
            }
        }
        return 0;
    }
}
