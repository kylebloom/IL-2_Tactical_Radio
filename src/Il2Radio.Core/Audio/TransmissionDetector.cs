namespace Il2Radio.Core.Audio;

/// <summary>
/// Turns a stream of audio-session peak samples into "someone is talking".
/// A steady engine is not talking, even after it gets louder: the floor follows a steady level,
/// so a throttle change only counts at the moment it jumps. Speech flutters — the peak rises and
/// falls within about half a second — and while it does, the floor stays put, so the transmission
/// stays busy for as long as the flutter lasts, not only at its onset.
/// </summary>
public sealed class TransmissionDetector
{
    private const int WindowSamples = 10;
    private const int MinSamples = 8;
    private const double MinSpread = 0.08;
    private const double MinRelativeSpread = 0.22;
    private const double MinTurn = 0.02;
    private const int MinTurns = 2;
    private const double Fall = 0.2;
    private const double Rise = 0.005;

    private readonly double _ratio;
    private readonly double _floor;
    private readonly Queue<double> _window = new();
    private double _baseline = -1;

    public TransmissionDetector(double ratio, double floor)
    {
        _ratio = ratio;
        _floor = floor;
    }

    /// <summary>The steady level the onset test is measured against. Negative until the first sample.</summary>
    public double Baseline => _baseline;

    /// <summary>One peak sample, about twenty times a second. True when this sample is part of a transmission.</summary>
    public bool Observe(double peak)
    {
        if (double.IsNaN(peak) || peak < 0)
            peak = 0;

        _window.Enqueue(peak);
        while (_window.Count > WindowSamples)
            _window.Dequeue();

        bool flutter = Fluttering();
        if (_baseline < 0)
            _baseline = peak;
        else if (peak < _baseline)
            _baseline += (peak - _baseline) * Fall;
        else if (!flutter)
            _baseline += (peak - _baseline) * Rise;

        bool audible = peak >= _floor;
        bool onset = audible && peak > _baseline * _ratio;
        return onset || (audible && flutter);
    }

    /// <summary>
    /// The recent peaks go up and down, by enough to be speech rather than a steady engine or a smooth
    /// power change. A ramp in one direction has no turns, so it is not speech.
    /// </summary>
    private bool Fluttering()
    {
        if (_window.Count < MinSamples)
            return false;

        double min = double.MaxValue, max = 0, sum = 0;
        double previous = 0, previousDelta = 0;
        int turns = 0, i = 0;
        foreach (double sample in _window)
        {
            if (sample < min)
                min = sample;
            if (sample > max)
                max = sample;
            sum += sample;
            if (i > 0)
            {
                double delta = sample - previous;
                if (i > 1 && delta * previousDelta < 0 && Math.Abs(delta) >= MinTurn && Math.Abs(previousDelta) >= MinTurn)
                    turns++;
                previousDelta = delta;
            }

            previous = sample;
            i++;
        }

        double spread = max - min;
        double level = sum / _window.Count;
        return turns >= MinTurns && spread >= MinSpread && spread >= level * MinRelativeSpread;
    }
}
