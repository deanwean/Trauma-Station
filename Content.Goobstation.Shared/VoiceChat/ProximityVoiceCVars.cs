// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Configuration;

namespace Content.Goobstation.Shared.VoiceChat;

[CVarDefs]
public static class ProximityVoiceCVars
{
    public static readonly CVarDef<bool> Enabled = CVarDef.Create(
        "voice.enabled", false, CVar.SERVER | CVar.REPLICATED, "Enable proximity microphone chat.");

    public static readonly CVarDef<int> Range = CVarDef.Create(
        "voice.range", 10, CVar.SERVER | CVar.REPLICATED, "Maximum proximity voice range in tiles.");

    public static readonly CVarDef<bool> RadioEnabled = CVarDef.Create(
        "voice.radio_enabled", true, CVar.SERVERONLY, "Allow microphone transmissions over radio channels.");

    public static readonly CVarDef<bool> RadioCommon = CVarDef.Create(
        "voice.radio_common", true, CVar.SERVERONLY, "Allow microphone transmissions on the Common radio channel.");

    public static readonly CVarDef<float> Volume = CVarDef.Create(
        "voice.volume", 1f, CVar.CLIENTONLY | CVar.ARCHIVE, "Playback volume for voice chat.");

    public static readonly CVarDef<float> RadioVolume = CVarDef.Create(
        "voice.radio_volume", 1f, CVar.CLIENTONLY | CVar.ARCHIVE, "Additional volume multiplier for headset voice chat.");

    public static readonly CVarDef<bool> Deafened = CVarDef.Create(
        "voice.deafened", false, CVar.CLIENTONLY | CVar.ARCHIVE, "Mute all incoming voice chat.");

    public static readonly CVarDef<bool> HearSelf = CVarDef.Create(
        "voice.hear_self", false, CVar.CLIENTONLY | CVar.ARCHIVE, "Play your own transmitted voice.");

    public static readonly CVarDef<bool> ShowIndicators = CVarDef.Create(
        "voice.indicators", true, CVar.CLIENTONLY | CVar.ARCHIVE, "Show VC activity on the map and in the speaker list.");

    public static readonly CVarDef<string> MutedRadio = CVarDef.Create(
        "voice.muted_radio", "", CVar.CLIENTONLY | CVar.ARCHIVE, "Radio voice channels muted on this client.");

    public static readonly CVarDef<string> PublicUrl = CVarDef.Create(
        "voice.public_url", "", CVar.SERVERONLY, "Public HTTPS URL of the /voice page. Loopback HTTP is supported for local testing.");
}
