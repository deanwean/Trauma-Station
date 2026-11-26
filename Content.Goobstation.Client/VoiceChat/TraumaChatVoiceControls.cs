// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Client.UserInterface.Systems.Chat.Controls;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Goobstation.Client.VoiceChat;

internal sealed class TraumaChatVoiceControls : IChatVoiceControls
{
    public IEnumerable<Control> CreateControls()
    {
        yield return new VoiceChannelControl();
    }

    private sealed class VoiceChannelControl : BoxContainer
    {
        private readonly VoiceRadioButton _radio = new();

        public VoiceChannelControl()
        {
            SeparationOverride = 4;
            _radio.StyleClasses.Add(ChannelSelectorItemButton.StyleClassChatSelectorOptionButton);
            AddChild(_radio);
            AddChild(new VoiceMicButton());
            AddChild(new VoiceDeafenButton());
        }

        protected override void FrameUpdate(FrameEventArgs args)
        {
            base.FrameUpdate(args);
            _radio.UpdateState();
        }
    }
}
