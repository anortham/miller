namespace Miller.Testing.Daemon;

public sealed record CtDiskAccountingSnapshot(
    long TotalAllocatedBytes,
    long BudgetBytes,
    int RootsTotal,
    int RootsMeasured,
    bool OverBudget,
    bool FullyMeasured,
    DateTimeOffset EvaluatedAt,
    long ReapDebtBytes = 0,
    string State = "available");
