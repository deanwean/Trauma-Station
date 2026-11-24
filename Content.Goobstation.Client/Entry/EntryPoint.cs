// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Client.IoC;
using Content.Goobstation.Client.Polls;
using Content.Goobstation.Client.VoiceChat;
using Content.Goobstation.Common.ServerCurrency;
using Content.Goobstation.Shared.VoiceChat;
using Robust.Shared.ContentPack;
using Robust.Shared.Timing;

namespace Content.Goobstation.Client.Entry;

public sealed partial class EntryPoint : GameClient
{
    [Dependency] private PollManager _pollManager = default!;
    [Dependency] private ICommonCurrencyManager _currMan = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private IEntityManager _entities = default!;

    public override void PreInit()
    {
        ContentGoobClientIoC.Register(Dependencies);
    }

    public override void Init()
    {
        Dependencies.BuildGraph();
        Dependencies.InjectDependencies(this);
    }

    public override void PostInit()
    {
        base.PostInit();

        _pollManager.Initialize();
        _currMan.Initialize();
        // Net messages are registered once per client, while entity systems restart on reconnect.
        _net.RegisterNetMessage<MsgProximityVoiceFrame>(OnVoiceFrame, NetMessageAccept.Client);
    }

    private void OnVoiceFrame(MsgProximityVoiceFrame message)
    {
        if (_entities.TrySystem<ProximityVoiceSystem>(out var voice))
            voice.ReceiveFrame(message);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _currMan.Shutdown();
    }
}
