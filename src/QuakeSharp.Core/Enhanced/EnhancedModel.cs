namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// A model the enhanced mode has seen, by the capture's number for it (design.md §11.6): a brush model made ready
/// (<see cref="Brush"/>), or a submodel of the level's world ("*n": <see cref="Parent"/>'s submodel
/// <see cref="Submodel"/>), an alias model or a sprite. Never written after it is made.
/// </summary>
public sealed class EnhancedModel
{
    public required string Name { get; init; }

    public EnhancedBrushModel? Brush { get; init; }

    public EnhancedAliasModel? Alias { get; init; }

    public EnhancedSpriteModel? Sprite { get; init; }

    /// <summary>For a submodel, the number of the model it belongs to (the world); 0 otherwise.</summary>
    public int Parent { get; init; }

    public int Submodel { get; init; }
}

/// <summary>
/// One numbering of the models (<see cref="EnhancedFrame.Models"/>): a capture starts one when it is made and with each
/// new level. Compared by reference, so two numberings are never the same, though each counts from 1: a capture made
/// when the mode is switched on again numbers the same level's models in another order, and what the render thread
/// made of the last capture's numbers must go.
/// </summary>
public sealed class EnhancedModelNumbering;
