using System;
using System.Collections.Generic;

namespace AetherFrame.Domain.Characters;

public sealed class CharacterBinding
{
    public int Version { get; set; } = 1;

    public ulong ContentId { get; set; }

    public Guid? ActiveProfileId { get; set; }

    public List<Guid> ProfileIds { get; set; } = new();

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
