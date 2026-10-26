using System.Buffers.Binary;
using System.Text;
using Il2Radio.Core.Osd;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

/// <summary>RTSS shared-memory OSD slot handling and text formatting, against a fake mapping.</summary>
public class RtssOsdTests
{
    private const string Owner = "IL-2 Tactical Radio";
    private const int ArrOffset = 64, EntrySize = RtssOsd.OsdExOffset + RtssOsd.OsdExSize, Slots = 4;

    private static byte[] FakeMemory(uint version = 0x00020015, uint signature = RtssOsd.Signature)
    {
        var memory = new byte[ArrOffset + EntrySize * Slots];
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(offset), value);
        Put(RtssOsd.SignatureOffset, signature);
        Put(RtssOsd.VersionOffset, version);
        Put(RtssOsd.OsdEntrySizeOffset, EntrySize);
        Put(RtssOsd.OsdArrOffsetOffset, ArrOffset);
        Put(RtssOsd.OsdArrSizeOffset, Slots);
        return memory;
    }

    private static string Field(byte[] memory, int slot, int offset, int size)
    {
        var field = memory.AsSpan(ArrOffset + slot * EntrySize + offset, size);
        int end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }

    private static string OwnerOf(byte[] memory, int slot) => Field(memory, slot, RtssOsd.OwnerOffset, RtssOsd.OwnerSize);
    private static string TextOf(byte[] memory, int slot) => Field(memory, slot, RtssOsd.OsdExOffset, RtssOsd.OsdExSize);
    private static uint Frame(byte[] memory) => BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(RtssOsd.OsdFrameOffset));

    private static void Claim(byte[] memory, int slot, string owner) =>
        Encoding.Latin1.GetBytes(owner).CopyTo(memory, ArrOffset + slot * EntrySize + RtssOsd.OwnerOffset);

    [Fact]
    public void First_write_claims_the_first_free_slot_after_the_primary_one()
    {
        var memory = FakeMemory();

        Assert.Equal(RtssWriteResult.Written, RtssOsd.Write(memory, Owner, "Gert Knoll: Flight, report your position."));

        Assert.Equal("", OwnerOf(memory, 0)); // slot 0 is reserved for Afterburner
        Assert.Equal(Owner, OwnerOf(memory, 1));
        Assert.Equal("Gert Knoll: Flight, report your position.", TextOf(memory, 1));
        Assert.Equal(1u, Frame(memory));
    }

    [Fact]
    public void Later_writes_reuse_the_owned_slot_and_skip_other_clients()
    {
        var memory = FakeMemory();
        Claim(memory, 1, "SomeOtherTool");
        RtssOsd.Write(memory, Owner, "a much longer first text");

        RtssOsd.Write(memory, Owner, "second");

        Assert.Equal("SomeOtherTool", OwnerOf(memory, 1));
        Assert.Equal(Owner, OwnerOf(memory, 2));
        Assert.Equal("second", TextOf(memory, 2)); // no leftovers from the longer text
        Assert.Equal(Owner, Assert.Single(RtssOsd.Owners(memory), o => o == Owner));
    }

    [Fact]
    public void No_free_slot_is_reported_and_nothing_is_overwritten()
    {
        var memory = FakeMemory();
        for (int slot = 1; slot < Slots; slot++)
            Claim(memory, slot, $"tool{slot}");

        Assert.Equal(RtssWriteResult.NoFreeSlot, RtssOsd.Write(memory, Owner, "x"));
        Assert.Equal(0u, Frame(memory));
    }

    [Theory]
    [InlineData(0u, 0x00020015u)]               // no signature: RTSS not running
    [InlineData(0xDEADu, 0x00020015u)]          // marked for deallocation
    [InlineData(RtssOsd.Signature, 0x00010005u)] // v1 layout
    public void Invalid_header_means_not_running(uint signature, uint version)
    {
        Assert.Equal(RtssWriteResult.NotRunning, RtssOsd.Write(FakeMemory(version, signature), Owner, "x"));
        Assert.Equal(RtssWriteResult.NotRunning, RtssOsd.Write(new byte[16], Owner, "x"));
    }

    [Fact]
    public void Slot_table_outside_the_mapping_is_rejected()
    {
        var memory = FakeMemory();
        BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(RtssOsd.OsdArrSizeOffset), 1000);

        Assert.Equal(RtssWriteResult.NotRunning, RtssOsd.Write(memory, Owner, "x"));
    }

    [Fact]
    public void Busy_renderer_lock_defers_the_write_and_is_left_set()
    {
        var memory = FakeMemory();
        BinaryPrimitives.WriteInt32LittleEndian(memory.AsSpan(RtssOsd.BusyOffset), 1);

        Assert.Equal(RtssWriteResult.Busy, RtssOsd.Write(memory, Owner, "x"));
        Assert.Equal("", TextOf(memory, 1));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(memory.AsSpan(RtssOsd.BusyOffset)));
    }

    [Fact]
    public void Write_releases_its_own_lock()
    {
        var memory = FakeMemory();
        RtssOsd.Write(memory, Owner, "x");
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(memory.AsSpan(RtssOsd.BusyOffset)));
    }

    [Fact]
    public void Pre_2_7_servers_get_the_256_byte_legacy_text_field()
    {
        var memory = FakeMemory(version: 0x00020005);

        RtssOsd.Write(memory, Owner, new string('x', 400));

        Assert.Equal(255, Field(memory, 1, RtssOsd.OsdTextOffset, RtssOsd.OsdTextSize).Length);
        Assert.Equal("", TextOf(memory, 1));
        Assert.Equal(256, RtssOsd.TextCapacity(0x00020005));
    }

    [Fact]
    public void Release_clears_only_our_slot_and_bumps_the_frame()
    {
        var memory = FakeMemory();
        Claim(memory, 1, "SomeOtherTool");
        RtssOsd.Write(memory, Owner, "hello");
        uint frame = Frame(memory);

        Assert.Equal(1, RtssOsd.Release(memory, Owner));

        Assert.Equal("", OwnerOf(memory, 2));
        Assert.Equal("", TextOf(memory, 2));
        Assert.Equal("SomeOtherTool", OwnerOf(memory, 1));
        Assert.Equal(frame + 1, Frame(memory));
        Assert.Equal(0, RtssOsd.Release(memory, Owner));
    }

    private static VisibleChatLine Visible(ChatLineKind kind, string speaker, string text, int coalition = 2, float alpha = 1) =>
        new(new ChatLine(kind, speaker, text, coalition, TimeSpan.Zero), DateTime.UnixEpoch, alpha);

    [Fact]
    public void Plain_format_is_speaker_colon_text_one_line_each()
    {
        var text = RtssOsd.Format(
        [
            Visible(ChatLineKind.Request, "Gert Knoll", "All friendlies, report positions."),
            Visible(ChatLineKind.System, "", "no reply", -1),
        ], tags: false);

        Assert.Equal("Gert Knoll: All friendlies, report positions.\nno reply", text);
    }

    [Fact]
    public void Tagged_format_uses_il2s_single_message_colour_with_sdk_colour_tags()
    {
        var text = RtssOsd.Format(
        [
            Visible(ChatLineKind.Reply, "Heinz Koelzig", "Number one, you are with us."),
            Visible(ChatLineKind.System, "", "no reply", -1),
        ], tags: true);

        Assert.Equal("<C=E0E0DC>Heinz Koelzig: Number one, you are with us.<C>\n<C=A8A8A4>no reply<C>", text);
    }

    [Fact]
    public void Fading_lines_are_dimmed()
    {
        var text = RtssOsd.Format([Visible(ChatLineKind.Request, "Gert Knoll", "x", alpha: 0)], tags: true);

        Assert.StartsWith("<C=383837>", text); // E0E0DC at 25 %
    }

    [Fact]
    public void Long_replies_wrap_under_the_text_column()
    {
        var text = RtssOsd.Format(
            [Visible(ChatLineKind.Reply, "Heinz Koelzig", "Number one, five aircraft together, bearing one-one-zero, approximately 18 kilometres, altitude 2700 metres.")],
            tags: false, wrapColumns: 60);

        var lines = text.Split('\n');
        Assert.True(lines.Length > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 60, l));
        Assert.All(lines.Skip(1), l => Assert.StartsWith(new string(' ', "Heinz Koelzig: ".Length), l));
    }

    [Fact]
    public void Tag_characters_in_text_cannot_inject_tags()
    {
        var text = RtssOsd.Format([Visible(ChatLineKind.Reply, "A<B>", "x <C=FF0000> y")], tags: false);

        Assert.DoesNotContain('<', text);
    }

    [Fact]
    public void Dashes_outside_latin1_become_hyphens()
    {
        var text = RtssOsd.Format([Visible(ChatLineKind.Info, "Num3", "Separated — request rejoin")], tags: false);

        Assert.Equal("Num3: Separated - request rejoin", text);
    }

    [Fact]
    public void Oldest_lines_are_dropped_to_fit_the_slot()
    {
        var lines = Enumerable.Range(0, 10).Select(i => Visible(ChatLineKind.Reply, $"P{i}", new string('x', 40))).ToList();

        var text = RtssOsd.Format(lines, tags: false, capacity: 150);

        Assert.True(Encoding.Latin1.GetByteCount(text) < 150);
        Assert.EndsWith("P9: " + new string('x', 40), text);
        Assert.DoesNotContain("P0:", text);
    }

    [Fact]
    public void Oldest_lines_are_dropped_to_fit_the_row_limit()
    {
        var lines = Enumerable.Range(0, 5).Select(i => Visible(ChatLineKind.Reply, $"P{i}", "x")).ToList();

        var text = RtssOsd.Format(lines, tags: true, maxRows: 2);

        Assert.Equal(2, RtssOsd.VisibleSize(text).Lines);
        Assert.Contains("P4: x", text);
        Assert.DoesNotContain("P2:", text);
    }
}

/// <summary>Which radio-log lines are visible when, shared by all chat outputs.</summary>
public class ChatTimelineTests
{
    private static readonly DateTime T0 = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    private static ChatLine Line(ChatLineKind kind, string text, double at) => new(kind, "x", text, 2, TimeSpan.FromSeconds(at));

    private static IReadOnlyList<ChatLine> Exchange() =>
    [
        Line(ChatLineKind.Request, "call", 0),
        Line(ChatLineKind.Reply, "one", 1.2),
        Line(ChatLineKind.Reply, "two", 5),
    ];

    private static string[] Texts(ChatTimeline t, double at) => t.Snapshot(T0.AddSeconds(at)).Select(v => v.Line.Text).ToArray();

    [Fact]
    public void Lines_appear_at_their_delay_and_expire_after_hold_and_fade()
    {
        var timeline = new ChatTimeline(holdSeconds: 14, fadeSeconds: 2);
        timeline.Post(Exchange(), T0);

        Assert.Equal(["call"], Texts(timeline, 0.5));
        Assert.Equal(["call", "one", "two"], Texts(timeline, 5));
        Assert.Equal(0.5f, timeline.Snapshot(T0.AddSeconds(15)).First().Alpha, 3);
        Assert.Equal(["two"], Texts(timeline, 19.5));
        Assert.Empty(Texts(timeline, 21.5));
    }

    [Fact]
    public void A_new_request_drops_lines_not_yet_shown()
    {
        var timeline = new ChatTimeline();
        timeline.Post(Exchange(), T0);
        Texts(timeline, 2);

        timeline.Post([Line(ChatLineKind.Request, "next call", 0)], T0.AddSeconds(2));

        Assert.Equal(["call", "one", "next call"], Texts(timeline, 10));
    }

    [Fact]
    public void Voice_synced_replies_wait_for_their_transmission()
    {
        var timeline = new ChatTimeline();
        timeline.Post(Exchange(), T0, syncToVoice: true);

        Assert.Equal(["call"], Texts(timeline, 6)); // past the estimates, but not yet spoken
        timeline.Spoken("one", T0.AddSeconds(7));
        Assert.Equal(["call", "one"], Texts(timeline, 7));
        timeline.Spoken("two", T0.AddSeconds(11));
        Assert.Equal(["call", "one", "two"], Texts(timeline, 11));
    }

    [Fact]
    public void Voice_synced_replies_still_appear_if_the_voice_never_plays_them()
    {
        var timeline = new ChatTimeline();
        timeline.Post(Exchange(), T0, syncToVoice: true);

        Assert.Equal(["call", "one", "two"], Texts(timeline, 5 + ChatTimeline.VoiceTimeout.TotalSeconds));
    }

    [Fact]
    public void Only_the_newest_lines_are_kept()
    {
        var timeline = new ChatTimeline(maxLines: 2);
        timeline.Post(Exchange(), T0);

        Assert.Equal(["one", "two"], Texts(timeline, 6));
    }

    /// <summary>Three exchanges of three lines, all shown by t = 30 s, the first long expired from the live view.</summary>
    private static ChatTimeline WithNineLines(int maxLines = 4)
    {
        var timeline = new ChatTimeline(holdSeconds: 14, fadeSeconds: 2, maxLines: maxLines);
        for (int i = 0; i < 3; i++)
        {
            timeline.Post([Line(ChatLineKind.Request, $"call{i}", 0), Line(ChatLineKind.Reply, $"one{i}", 1), Line(ChatLineKind.Reply, $"two{i}", 2)], T0.AddSeconds(i * 10));
            timeline.Snapshot(T0.AddSeconds(i * 10 + 5));
        }
        return timeline;
    }

    [Fact]
    public void History_pages_back_from_the_newest_lines_with_a_footer()
    {
        var timeline = WithNineLines(maxLines: 4); // pages of 3 lines + footer
        var at = T0.AddSeconds(40);

        timeline.Scroll(+1, at);
        var page = timeline.Snapshot(at);

        Assert.Equal(["call2", "one2", "two2", "Radio log 7-9 of 9"], page.Select(v => v.Line.Text));
        Assert.EndsWith(" x", page[0].Line.Speaker); // stamped with the time it was shown
        Assert.All(page, v => Assert.Equal(1f, v.Alpha));

        timeline.Scroll(+1, at);
        timeline.Scroll(+1, at);
        timeline.Scroll(+1, at); // already at the oldest page
        Assert.Equal(["call0", "one0", "two0", "Radio log 1-3 of 9"], Texts(timeline, 40));
    }

    [Fact]
    public void Paging_newer_past_the_newest_page_returns_to_the_live_lines()
    {
        var timeline = WithNineLines();
        var at = T0.AddSeconds(25);

        timeline.Scroll(+1, at);
        timeline.Scroll(+1, at);
        timeline.Scroll(-1, at);
        Assert.Equal("Radio log 7-9 of 9", Texts(timeline, 25).Last());
        timeline.Scroll(-1, at);

        Assert.Equal(["two1", "call2", "one2", "two2"], Texts(timeline, 25)); // the newest 4 live lines
    }

    [Fact]
    public void Paging_newer_from_a_short_oldest_page_shows_the_newest_page_not_the_empty_live_view()
    {
        // Five lines in pages of three: the oldest page is clamped to lines 1-3, only two lines
        // back from the newest page. Paging newer from it must show the newest page again, not
        // jump past it to the live view, which by now has faded to nothing (looks "hidden").
        var timeline = new ChatTimeline(holdSeconds: 14, fadeSeconds: 2, maxLines: 4);
        timeline.Post([Line(ChatLineKind.Request, "call0", 0), Line(ChatLineKind.Reply, "one0", 1), Line(ChatLineKind.Reply, "two0", 2)], T0);
        timeline.Snapshot(T0.AddSeconds(5));
        timeline.Post([Line(ChatLineKind.Request, "call1", 0), Line(ChatLineKind.Reply, "one1", 1)], T0.AddSeconds(10));
        timeline.Snapshot(T0.AddSeconds(15));
        var at = T0.AddSeconds(60);
        Assert.Empty(Texts(timeline, 60));

        timeline.Scroll(+1, at);
        Assert.Equal("Radio log 3-5 of 5", Texts(timeline, 60).Last());
        timeline.Scroll(+1, at);
        Assert.Equal("Radio log 1-3 of 5", Texts(timeline, 60).Last());
        timeline.Scroll(-1, at);
        Assert.Equal(["two0", "call1", "one1", "Radio log 3-5 of 5"], Texts(timeline, 60));
        timeline.Scroll(-1, at);
        Assert.Empty(Texts(timeline, 60));
    }

    [Fact]
    public void Paging_back_and_forward_revisits_the_same_pages()
    {
        var timeline = new ChatTimeline(holdSeconds: 14, fadeSeconds: 2, maxLines: 4); // pages of 3
        for (int i = 0; i < 10; i++)
        {
            timeline.Post([Line(ChatLineKind.Request, $"call{i}", 0)], T0.AddSeconds(i));
            timeline.Snapshot(T0.AddSeconds(i));
        }
        var at = T0.AddSeconds(60);

        var back = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            timeline.Scroll(+1, at);
            back.Add(Texts(timeline, 60).Last());
        }
        Assert.Equal(["Radio log 8-10 of 10", "Radio log 5-7 of 10", "Radio log 2-4 of 10", "Radio log 1-3 of 10"], back);

        var forward = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            timeline.Scroll(-1, at);
            forward.Add(Texts(timeline, 60).Last());
        }
        Assert.Equal(["Radio log 2-4 of 10", "Radio log 5-7 of 10", "Radio log 8-10 of 10"], forward);
    }

    [Fact]
    public void History_keeps_lines_beyond_the_line_limit_and_the_fade()
    {
        var timeline = WithNineLines(maxLines: 20);
        var at = T0.AddSeconds(200);

        Assert.Empty(Texts(timeline, 200));
        timeline.Scroll(+1, at);

        Assert.Equal(10, Texts(timeline, 200).Length);
    }

    [Fact]
    public void Browsing_views_close_after_the_hold_time_or_on_a_new_request()
    {
        var timeline = WithNineLines();
        timeline.Scroll(+1, T0.AddSeconds(30));
        Assert.Equal("Radio log 7-9 of 9", Texts(timeline, 43).Last());
        Assert.DoesNotContain(Texts(timeline, 45), t => t.StartsWith("Radio log"));

        timeline.Scroll(+1, T0.AddSeconds(50));
        timeline.Post([Line(ChatLineKind.Request, "new call", 0)], T0.AddSeconds(51));
        Assert.Equal(["new call"], Texts(timeline, 51));
    }

    [Fact]
    public void Menu_replaces_the_live_lines_until_toggled_off()
    {
        var timeline = new ChatTimeline();
        timeline.Post(Exchange(), T0);
        ChatLine[] menu = [new(ChatLineKind.Info, "Num1", "Request friendly positions", -1, TimeSpan.Zero)];

        timeline.ToggleMenu(menu, T0.AddSeconds(6));
        Assert.Equal(["Request friendly positions"], Texts(timeline, 6));

        timeline.ToggleMenu(menu, T0.AddSeconds(7));
        Assert.Equal(["call", "one", "two"], Texts(timeline, 7));
    }

    [Fact]
    public void History_stamps_the_mission_clock_and_keeps_it_when_the_mission_changes()
    {
        var timeline = new ChatTimeline(holdSeconds: 30, fadeSeconds: 2, maxLines: 4);
        var clock = new MissionClock();
        timeline.MissionTime = clock;
        timeline.Post([Line(ChatLineKind.Request, "call", 0)], T0);
        timeline.Scroll(+1, T0.AddSeconds(1));

        // The line was shown at T0, before this sample. 14:12:00 at T0+2s puts it at 14:11:58.
        clock.Sync(new DateTime(1942, 8, 26, 14, 12, 0), T0.AddSeconds(2), authoritative: true);
        var page = timeline.Snapshot(T0.AddSeconds(3));
        Assert.Equal("14:11:58 x", page[0].Line.Speaker);

        timeline.Post([new ChatLine(ChatLineKind.System, "", "no reply", -1, TimeSpan.Zero)], T0.AddSeconds(3));
        timeline.Scroll(+1, T0.AddSeconds(4));
        page = timeline.Snapshot(T0.AddSeconds(4));
        Assert.Equal("14:12:01 no reply", page[^2].Line.Text);

        timeline.FreezeMissionTime();
        clock.Reset(T0.AddSeconds(10));
        clock.Sync(new DateTime(1942, 8, 26, 18, 0, 0), T0.AddSeconds(10), authoritative: true);
        page = timeline.Snapshot(T0.AddSeconds(11));
        Assert.Equal("14:11:58 x", page[0].Line.Speaker);
        Assert.Equal("14:12:01 no reply", page[1].Line.Text);
    }
}
