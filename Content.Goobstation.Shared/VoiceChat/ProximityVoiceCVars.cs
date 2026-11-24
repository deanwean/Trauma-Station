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

    public static readonly CVarDef<string> PublicUrl = CVarDef.Create(
        "voice.public_url", "", CVar.SERVERONLY, "Public HTTPS URL of the /voice page. Loopback HTTP is supported for local testing.");
}
