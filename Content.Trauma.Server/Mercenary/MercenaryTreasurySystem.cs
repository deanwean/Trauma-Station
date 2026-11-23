// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Cargo.Components;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Stacks;
using Content.Shared.Storage.Components;
using Robust.Shared.Prototypes;

namespace Content.Trauma.Server.Mercenary;

public sealed partial class MercenaryTreasurySystem : EntitySystem
{
    private static readonly ProtoId<StackPrototype> Credit = "Credit";

    private bool _treasuryExisted;

    public override void Initialize()
    {
        SubscribeLocalEvent<MercenaryTreasuryComponent, MapInitEvent>((_, _, _) => _treasuryExisted = true);
        SubscribeLocalEvent<MercenaryTreasuryComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEnd);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _treasuryExisted = false);
    }

    private void OnExamined(Entity<MercenaryTreasuryComponent> vault, ref ExaminedEvent args)
    {
        if (TryComp<EntityStorageComponent>(vault, out var storage))
            args.PushText(Loc.GetString("mercenary-treasury-balance", ("amount", GetBalance(storage))));
    }

    private void OnRoundEnd(ref RoundEndTextAppendEvent args)
    {
        if (!_treasuryExisted)
            return;

        long total = 0;
        var vaults = EntityQueryEnumerator<MercenaryTreasuryComponent, EntityStorageComponent>();
        while (vaults.MoveNext(out _, out var storage))
            total += GetBalance(storage);

        args.AddLine(Loc.GetString("mercenary-round-end-balance", ("amount", total)));
    }

    private long GetBalance(EntityStorageComponent storage)
    {
        long balance = 0;
        foreach (var item in storage.Contents.ContainedEntities)
        {
            if (HasComp<CashComponent>(item) && TryComp<StackComponent>(item, out var stack) && stack.StackTypeId == Credit)
                balance += stack.Count;
        }

        return balance;
    }
}
