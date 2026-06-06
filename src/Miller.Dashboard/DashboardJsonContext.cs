using System.Text.Json.Serialization;
using Miller.Server.Workspaces;

namespace Miller.Dashboard;

[JsonSerializable(typeof(DashboardWorkspaceIndex))]
[JsonSerializable(typeof(IReadOnlyList<DashboardWorkspaceRow>))]
[JsonSerializable(typeof(DashboardTelemetrySummary))]
[JsonSerializable(typeof(DashboardSnapshot))]
[JsonSerializable(typeof(WorkspaceRefreshResult))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext;
