using System.Diagnostics;

namespace CupriFace.Shell;

/// <summary>
/// Minimal frame profiler. DESIGN.md §7.7 — "measurement is non-optional": the HUD
/// exists from day one so every later change is measured against a frame budget.
/// Tracks CPU frame time (draw-callback duration) and an EMA-smoothed FPS with
/// zero per-frame allocation (§7.4).
/// </summary>
public sealed class FrameStats
{
    private readonly Stopwatch _cpu = new();
    private double _emaDeltaSeconds;

    /// <summary>Total frames rendered since start.</summary>
    public long FrameCount { get; private set; }

    /// <summary>CPU time spent in the last frame's draw callback, in milliseconds.</summary>
    public double CpuFrameMs { get; private set; }

    /// <summary>Smoothed frames per second (EMA over frame deltas).</summary>
    public double Fps => _emaDeltaSeconds > 0 ? 1.0 / _emaDeltaSeconds : 0.0;

    internal void BeginFrame(double deltaSeconds)
    {
        // Exponential moving average smooths the readout without keeping history.
        const double alpha = 0.1;
        if (deltaSeconds > 0)
        {
            _emaDeltaSeconds = _emaDeltaSeconds == 0
                ? deltaSeconds
                : _emaDeltaSeconds + alpha * (deltaSeconds - _emaDeltaSeconds);
        }
        _cpu.Restart();
    }

    internal void EndFrame()
    {
        _cpu.Stop();
        CpuFrameMs = _cpu.Elapsed.TotalMilliseconds;
        FrameCount++;
    }
}
