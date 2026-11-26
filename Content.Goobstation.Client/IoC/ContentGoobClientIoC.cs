// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Client.JumpScare;
using Content.Goobstation.Client.Polls;
using Content.Goobstation.Client.Redial;
using Content.Goobstation.Client.ServerCurrency;
using Content.Client.UserInterface.Systems.Chat.Controls;
using Content.Goobstation.Client.VoiceChat;
using Content.Goobstation.Common.ServerCurrency;
using Content.Goobstation.Shared.JumpScare;
using Robust.Shared.IoC;

namespace Content.Goobstation.Client.IoC;

internal static class ContentGoobClientIoC
{
    internal static void Register(IDependencyCollection collection)
    {
        collection.Register<RedialManager>();
        collection.Register<PollManager>();
        collection.Register<IChatVoiceControls, TraumaChatVoiceControls>();
        collection.Register<IFullScreenImageJumpscare, ClientFullScreenImageJumpscare>();
        collection.Register<ICommonCurrencyManager, ClientCurrencyManager>();
    }
}
