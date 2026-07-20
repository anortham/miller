### Task E2: near_duplicate_group_count history metric + dashboard + report

**Files:**
- Modify: the history snapshot writer (per `docs/contracts/metrics-history-v1.md`; locate via `metrics history` CLI verb), `src/Miller.Dashboard/DashboardData.cs` (ReadLocalMetricsPanel, src/Miller.Dashboard/DashboardData.cs:982), report rollup (`miller report`) count surface
- Test: history writer tests, dashboard data tests, report tests alongside existing patterns

**Interfaces:**
- Consumes: E1's `NearDuplicateAnalyzer` output via the same data path MetricsTool uses.
- Produces: append-only metric name `near_duplicate_group_count` in history.db snapshots; dashboard trend sparkline (rides the existing trend mechanism — design says "dashboard sparkline free"); report rollup count. Count-level only per ADR-0002 — no per-symbol detail on the dashboard.

**Contract inputs:** `metrics-history-v1.md` (metric names are append-only); ADR-0002 dashboard boundary.

**File ownership:** Modify: history snapshot writer, `src/Miller.Dashboard/DashboardData.cs`, report rollup; tests alongside each

**Serialization required:** Yes (after E1)

**Dependency reason:** Consumes E1's analyzer output.

**What to build:** Trend surfacing for the new metric through the existing history/dashboard/report machinery.

**Acceptance criteria:**
- [ ] Snapshots record `near_duplicate_group_count`; `miller metrics history` surfaces it per contract
- [ ] Dashboard shows the trend at count level only; report rollup includes the count
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode
