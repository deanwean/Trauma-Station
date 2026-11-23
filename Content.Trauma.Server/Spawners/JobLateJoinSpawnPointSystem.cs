// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Shuttles.Systems;
using Content.Shared.GameTicking;
using Content.Shared.Spawners.Components;
using Content.Shared.Spawners.EntitySystems;
using Content.Shared.Station.Systems;
using Content.Trauma.Shared.Spawners;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Trauma.Server.Spawners;

/// <summary>
/// Sends late joiners to marked job spawn points before trying cryosleep or arrivals.
/// </summary>
public sealed partial class JobLateJoinSpawnPointSystem : EntitySystem
{
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private StationSpawningSystem _spawning = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawningEvent>(OnPlayerSpawning,
            before: [typeof(ContainerSpawnPointSystem), typeof(ServerArrivalsSystem), typeof(SpawnPointSystem)]);
    }

    private void OnPlayerSpawning(PlayerSpawningEvent args)
    {
        if (args.SpawnResult != null || args.Job == null || _ticker.RunLevel != GameRunLevel.InRound)
            return;

        var positions = new List<EntityCoordinates>();
        var query = EntityQueryEnumerator<JobLateJoinSpawnPointComponent, SpawnPointComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var point, out var xform))
        {
            if (point.SpawnType != SpawnPointType.Job || point.Job != args.Job)
                continue;

            if (args.Station != null && _station.GetOwningStation(uid, xform) != args.Station)
                continue;

            positions.Add(xform.Coordinates);
        }

        if (positions.Count == 0)
            return;

        args.SpawnResult = _spawning.SpawnPlayerMob(_random.Pick(positions),
            args.Job,
            args.HumanoidCharacterProfile,
            args.Station);
    }
}
