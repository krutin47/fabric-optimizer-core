using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Validation;

namespace DreamGlass.Optimizer.Approval;

public sealed record StockReference(string StockId, long Version);

/// <summary>
/// Immutable, auditable record of an employee choosing a candidate (spec §1, §10 "Employee choice"). The host
/// application persists it and atomically reserves <see cref="StockToReserve"/> with optimistic concurrency on
/// each version; authorisation is the host's responsibility.
/// </summary>
public sealed record PlanApproval
{
    public required string RunId { get; init; }
    public required string InputHash { get; init; }
    public required string CandidateId { get; init; }
    public required string CandidateContentHash { get; init; }
    public required int CandidateRank { get; init; }
    public required bool IsTopRanked { get; init; }
    public required string ApprovedBy { get; init; }
    public string? Reason { get; init; }
    public required DateTimeOffset ApprovedAtUtc { get; init; }
    public required string SolverVersion { get; init; }
    public required string ObjectiveVersion { get; init; }
    public required string ValidatorVersion { get; init; }
    public required IReadOnlyList<StockReference> StockToReserve { get; init; }
    public PurchaseLine? Purchase { get; init; }
}

public static class PlanSelection
{
    /// <summary>
    /// Re-validates the chosen candidate against the same snapshot (defence in depth) and records the choice.
    /// Choosing a lower-ranked plan is allowed but requires a reason.
    /// </summary>
    public static PlanApproval Approve(
        OptimizationResult result,
        OptimizationSnapshot snapshot,
        string candidateId,
        string approvedBy,
        string? reason,
        DateTimeOffset nowUtc,
        ICutPlanValidator? validator = null)
    {
        if (result.Status != RunStatus.Succeeded) throw new InvalidOperationException($"Run status {result.Status} has no approvable candidates.");
        if (string.IsNullOrWhiteSpace(approvedBy)) throw new ArgumentException("Approver identity is required.", nameof(approvedBy));
        var candidate = result.Candidates.FirstOrDefault(c => c.CandidateId == candidateId)
                        ?? throw new KeyNotFoundException($"Candidate '{candidateId}' is not part of run {result.RunId}.");
        var check = (validator ?? new CutPlanValidator()).Validate(snapshot, candidate);
        if (!check.Passed)
            throw new InvalidOperationException("Candidate failed independent validation and cannot be approved: " + string.Join("; ", check.Failures.Select(f => f.Code)));
        if (candidate.Rank != 1 && string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required when approving a candidate that is not ranked first.", nameof(reason));

        return new PlanApproval
        {
            RunId = result.RunId,
            InputHash = result.InputHash,
            CandidateId = candidate.CandidateId,
            CandidateContentHash = OptimizerJson.Hash(candidate),
            CandidateRank = candidate.Rank,
            IsTopRanked = candidate.Rank == 1,
            ApprovedBy = approvedBy,
            Reason = reason,
            ApprovedAtUtc = nowUtc.ToUniversalTime(),
            SolverVersion = result.SolverVersion,
            ObjectiveVersion = result.ObjectiveVersion,
            ValidatorVersion = check.ValidatorVersion,
            StockToReserve = candidate.Sources
                .Where(s => s.Kind == SourceKind.WarehouseRemnant)
                .Select(s => new StockReference(s.StockId!, s.StockVersion ?? 0))
                .ToList(),
            Purchase = candidate.Purchase,
        };
    }
}
