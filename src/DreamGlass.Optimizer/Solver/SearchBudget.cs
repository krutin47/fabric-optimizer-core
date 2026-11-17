using System.Diagnostics;
using DreamGlass.Optimizer.Contracts;

namespace DreamGlass.Optimizer.Solver;

/// <summary>Deadline, work-unit and cancellation checks between search operations (spec §6 runtime rules).</summary>
internal sealed class SearchBudget(long timeBudgetMs, long workLimit, ExecutionMode mode, CancellationToken ct)
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public long TimeBudgetMs { get; } = timeBudgetMs;
    public long WorkLimit { get; } = workLimit;
    public long Work { get; private set; }
    public ExecutionMode Mode { get; } = mode;
    public long ElapsedMs => _sw.ElapsedMilliseconds;
    public bool Cancelled => ct.IsCancellationRequested;
    public bool TimeExpired => _sw.ElapsedMilliseconds >= TimeBudgetMs;
    public bool WorkExpired => Work >= WorkLimit;
    public bool Expired => Cancelled || TimeExpired || WorkExpired;

    /// <summary>True once the time cap fired in deterministic mode (the result may then not be reproducible).</summary>
    public bool SafetyCapHit { get; private set; }

    public void Spend(long units = 1) => Work += units;

    /// <summary>Fraction of the dominant budget already used (work in deterministic mode, time otherwise).</summary>
    public double Used => Mode == ExecutionMode.Deterministic
        ? (double)Work / Math.Max(1, WorkLimit)
        : (double)_sw.ElapsedMilliseconds / Math.Max(1, TimeBudgetMs);

    public StopReason Reason()
    {
        if (Cancelled) return StopReason.Cancelled;
        if (TimeExpired)
        {
            if (Mode == ExecutionMode.Deterministic) SafetyCapHit = true;
            return StopReason.TimeBudget;
        }

        return WorkExpired ? StopReason.WorkBudget : StopReason.ScheduleExhausted;
    }

    public void NoteTimeCap()
    {
        if (Mode == ExecutionMode.Deterministic && TimeExpired) SafetyCapHit = true;
    }
}
