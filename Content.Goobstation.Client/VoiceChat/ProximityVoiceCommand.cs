// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Goobstation.Client.VoiceChat;

[AnyCommand]
public sealed partial class ProximityVoiceCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "voice";
    public string Description => Loc.GetString("trauma-voice-command-description");
    public string Help => Loc.GetString("trauma-voice-command-help");

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!_entities.TrySystem<ProximityVoiceSystem>(out var voice))
        {
            shell.WriteError(Loc.GetString("trauma-voice-unavailable"));
            return;
        }

        if (args.Length == 0)
            voice.RequestPairing(shell);
        else if (args is ["off"])
            voice.Leave(shell);
        else
            shell.WriteError(Help);
    }
}
