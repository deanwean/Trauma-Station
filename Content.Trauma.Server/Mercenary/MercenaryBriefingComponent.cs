// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Audio;

namespace Content.Trauma.Server.Mercenary;

[RegisterComponent]
public sealed partial class MercenaryBriefingComponent : Component
{
    [DataField(required: true)]
    public SoundSpecifier Sound = default!;
}
