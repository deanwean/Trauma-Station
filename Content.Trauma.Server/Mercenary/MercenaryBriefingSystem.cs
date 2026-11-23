// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Roles;
using Content.Shared.Antag;
using Content.Trauma.Common.Ghost;

namespace Content.Trauma.Server.Mercenary;

public sealed partial class MercenaryBriefingSystem : EntitySystem
{
    [Dependency] private AntagSelectionSystem _antag = default!;
    [Dependency] private RoleSystem _roles = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<MercenaryBriefingComponent, GhostRoleCreatedMindEvent>(OnGhostRoleCreatedMind);
    }

    private void OnGhostRoleCreatedMind(Entity<MercenaryBriefingComponent> mercenary, ref GhostRoleCreatedMindEvent args)
    {
        if (_roles.MindGetBriefing(args.Mind) is { } briefing)
            _antag.SendBriefing(args.Mob, briefing, Color.FromHex("#57da86"), mercenary.Comp.Sound);
    }
}
