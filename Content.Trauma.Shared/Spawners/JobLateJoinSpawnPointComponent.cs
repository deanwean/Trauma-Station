// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Trauma.Shared.Spawners;

/// <summary>
/// Allows a job's normal spawn point to take priority over arrivals when late joining.
/// Requires a SpawnPoint component with a specific job.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class JobLateJoinSpawnPointComponent : Component;
