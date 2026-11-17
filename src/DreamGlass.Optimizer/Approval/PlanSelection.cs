using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Validation;

namespace DreamGlass.Optimizer.Approval;

public sealed record StockReference(string StockId, long Version);

/// <summary>
/// Immutable, auditable record of an employee choosing a candidate (spec §1, handbook §97–98). The host application
/// persists it and atomically reserves <see cref="StockToReserve"/> with optimistic concurrency on each version;
/// authorisation and the reservation transaction are the host's responsibility.
/// </summary>
public sealed record PlanApproval
{
    public required string RunId { get; init; }
    public required string InputHash { get; init; }
    public required string SnapshotHash { get; init; }
    public string? OrganizationId { get; init; }
    public string? JobId { get; init; }
    public required string JobRevisionId { get; init; }
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

/// <summary>
/// The facts as they are now, re-read by the host at confirmation time (handbook §97: "compare live job revision,
/// stock versions and tenant state with the saved snapshot"). A plan made from an older state must not be approved.
/// </summary>
public sealed record LiveJobState
{
    public string? OrganizationId { get; init; }

    /// <summary>False when the organisation is suspended or closed: no business action may proceed.</summary>
    public bool OrganizationActive { get; init; } = true;

    /// <summary>The job's current revision.</summary>
    public string JobRevisionId { get; init; } = "";

    /// <summary>Current warehouse rows for at least the stock the plan uses.</summary>
    public IReadOnlyList<StockItem> Stock { get; init; } = [];
}

/// <summary>An approval that was refused, with a stable code and a message the employee can act on.</summary>
public sealed class PlanApprovalException(string code, string message) : InvalidOperationException(message)
{
    /// <summary>
    /// RUN_NOT_SUCCEEDED, CANDIDATE_INVALID, ORGANIZATION_INACTIVE, ORGANIZATION_MISMATCH, STALE_JOB_REVISION,
    /// STOCK_UNAVAILABLE or STALE_STOCK_VERSION.
    /// </summary>
    public string Code { get; } = code;
}

public static class PlanSelection
{
    /// <summary>
    /// Re-validates the chosen candidate against its snapshot (defence in depth), checks it against the live state
    /// when one is given, and records the choice. Choosing a lower-ranked plan is allowed but requires a reason.
    /// Validating a past snapshot is not enough to consume current stock: pass <paramref name="live"/> in production,
    /// and still reserve inside one database transaction.
    /// </summary>
    /// <exception cref="PlanApprovalException">The plan may not be approved; <see cref="PlanApprovalException.Code"/> says why.</exception>
    public static PlanApproval Approve(
        OptimizationResult result,
        OptimizationSnapshot snapshot,
        string candidateId,
        string approvedBy,
        string? reason,
        DateTimeOffset nowUtc,
        ICutPlanValidator? validator = null,
        LiveJobState? live = null)
    {
        if (result.Status != RunStatus.Succeeded)
            throw new PlanApprovalException("RUN_NOT_SUCCEEDED", $"Run status {result.Status} has no approvable candidates.");
        if (string.IsNullOrWhiteSpace(approvedBy)) throw new ArgumentException("Approver identity is required.", nameof(approvedBy));
        var candidate = result.Candidates.FirstOrDefault(c => c.CandidateId == candidateId)
                        ?? throw new KeyNotFoundException($"Candidate '{candidateId}' is not part of run {result.RunId}.");
        var check = (validator ?? new CutPlanValidator()).Validate(snapshot, candidate);
        if (!check.Passed)
            throw new PlanApprovalException("CANDIDATE_INVALID", "Candidate failed independent validation and cannot be approved: " + string.Join("; ", check.Failures.Select(f => f.Code).Distinct()));
        if (candidate.Rank != 1 && string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required when approving a candidate that is not ranked first.", nameof(reason));
        if (live is not null) CheckLive(snapshot, candidate, live);

        return new PlanApproval
        {
            RunId = result.RunId,
            InputHash = result.InputHash,
            SnapshotHash = candidate.SnapshotHash,
            OrganizationId = snapshot.OrganizationId,
            JobId = snapshot.JobId,
            JobRevisionId = snapshot.JobRevisionId,
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

    private static void CheckLive(OptimizationSnapshot snapshot, CandidateCutPlan candidate, LiveJobState live)
    {
        if (!live.OrganizationActive)
            throw new PlanApprovalException("ORGANIZATION_INACTIVE", "The organisation is not active, so no plan can be approved.");
        if (!string.Equals(live.OrganizationId, snapshot.OrganizationId, StringComparison.Ordinal))
            throw new PlanApprovalException("ORGANIZATION_MISMATCH", "This plan belongs to a different organisation.");
        if (!string.Equals(live.JobRevisionId, snapshot.JobRevisionId, StringComparison.Ordinal))
            throw new PlanApprovalException("STALE_JOB_REVISION",
                $"The job was changed after this plan was made (revision '{snapshot.JobRevisionId}' → '{live.JobRevisionId}'). The plan is kept as history; re-run the optimisation for the current job.");
        foreach (var src in candidate.Sources.Where(s => s.Kind == SourceKind.WarehouseRemnant))
        {
            var now = live.Stock.FirstOrDefault(s => s is not null && s.StockId == src.StockId);
            if (now is null || !now.Available)
                throw new PlanApprovalException("STOCK_UNAVAILABLE",
                    $"Remnant {src.StockId} is no longer available (reserved, used or removed since this plan was made). Re-run with current stock.");
            if (!string.Equals(now.OrganizationId, snapshot.OrganizationId, StringComparison.Ordinal))
                throw new PlanApprovalException("ORGANIZATION_MISMATCH", $"Remnant {src.StockId} belongs to a different organisation.");
            if (now.Version != src.StockVersion)
                throw new PlanApprovalException("STALE_STOCK_VERSION",
                    $"Remnant {src.StockId} changed after this plan was made (version {src.StockVersion} → {now.Version}). Re-run with current stock.");
        }
    }
}
