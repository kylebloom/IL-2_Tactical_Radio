using Il2Radio.Core.Audio;

namespace Il2Radio.Core.Tests;

/// <summary>
/// The radio-wait decision, without a sound card: a steady engine is clear, speech that flutters stays busy.
/// </summary>
public class TransmissionDetectorTests
{
    private const double Ratio = 1.8;
    private const double Floor = 0.04;

    private static TransmissionDetector Detector() => new(Ratio, Floor);

    [Fact]
    public void A_steady_engine_is_not_talking()
    {
        var detector = Detector();

        for (int i = 0; i < 40; i++)
            Assert.False(detector.Observe(0.40));
    }

    [Fact]
    public void A_louder_steady_engine_is_busy_only_while_the_floor_catches_up()
    {
        var detector = Detector();
        for (int i = 0; i < 20; i++)
            detector.Observe(0.40);

        Assert.True(detector.Observe(0.90));

        bool stillBusy = false;
        for (int i = 0; i < 400; i++)
            stillBusy = detector.Observe(0.90);

        Assert.False(stillBusy);
        Assert.InRange(detector.Baseline, 0.80, 0.90);
    }

    [Fact]
    public void A_gentle_power_increase_is_not_talking()
    {
        var detector = Detector();
        for (int i = 0; i < 30; i++)
            detector.Observe(0.50);

        // A ramp the floor can follow. A faster one trips the onset test, then clears once the new level is steady.
        for (int step = 1; step <= 40; step++)
            Assert.False(detector.Observe(0.50 + step * 0.001));
    }

    [Fact]
    public void A_fast_power_increase_clears_once_the_new_level_is_steady()
    {
        var detector = Detector();
        for (int step = 0; step <= 40; step++)
            detector.Observe(0.20 + step * 0.01);

        bool stillBusy = true;
        for (int i = 0; i < 400; i++)
            stillBusy = detector.Observe(0.60);

        Assert.False(stillBusy);
    }

    [Fact]
    public void Speech_that_flutters_stays_busy_and_does_not_become_the_floor()
    {
        var detector = Detector();
        for (int i = 0; i < 20; i++)
            detector.Observe(0.25);

        double[] syllable = [0.30, 0.62, 0.28, 0.66, 0.31, 0.70, 0.29, 0.64];
        bool talking = false;
        for (int i = 0; i < 40; i++)
            talking = detector.Observe(syllable[i % syllable.Length]);

        Assert.True(talking);
        Assert.InRange(detector.Baseline, 0.20, 0.40);

        bool cleared = false;
        for (int i = 0; i < 10; i++)
            cleared = !detector.Observe(0.25);
        Assert.True(cleared);
    }

    [Fact]
    public void Silence_below_the_floor_is_not_talking()
    {
        var detector = Detector();
        double[] syllable = [0.01, 0.03, 0.00, 0.02, 0.01, 0.03, 0.00, 0.02];

        for (int i = 0; i < 30; i++)
            Assert.False(detector.Observe(syllable[i % syllable.Length]));
    }
}
