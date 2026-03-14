using Robust.Shared.GameObjects;

namespace Content.Server.LLMPlayer;

/// <summary>
///     Marks an entity as being controlled by an LLM.
///     Stores per-entity conversation history and character context.
/// </summary>
[RegisterComponent]
public sealed partial class LLMPlayerComponent : Component
{
    /// <summary>
    ///     Conversation history for LLM context, stored as a list of (role, content) pairs.
    ///     Initialized per-entity when the component is added.
    /// </summary>
    [DataField]
    public List<LLMMessage> ConversationHistory = new();

    /// <summary>
    ///     Time accumulator for periodic LLM updates.
    /// </summary>
    public float TimeSinceLastUpdate;

    /// <summary>
    ///     Whether the LLM is currently waiting for a response.
    /// </summary>
    public bool AwaitingResponse;

    /// <summary>
    ///     The character's name for context building.
    /// </summary>
    public string CharacterName = string.Empty;

    /// <summary>
    ///     The character's species (e.g. Human, Dwarf, Reptilian).
    /// </summary>
    public string Species = string.Empty;

    /// <summary>
    ///     The character's age.
    /// </summary>
    public int Age;

    /// <summary>
    ///     The character's gender.
    /// </summary>
    public string Gender = string.Empty;

    /// <summary>
    ///     The character's job/role on the station.
    /// </summary>
    public string Job = string.Empty;
}

/// <summary>
///     Represents a single message in the LLM conversation history.
/// </summary>
[DataDefinition]
public sealed partial class LLMMessage
{
    [DataField]
    public string Role { get; set; } = string.Empty;

    [DataField]
    public string Content { get; set; } = string.Empty;

    public LLMMessage()
    {
    }

    public LLMMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }
}
