using Robust.Shared.GameObjects;

namespace Content.Server.LLMPlayer;

/// <summary>
///     Marks an entity as being controlled by an LLM.
///     Stores per-entity conversation history for context.
/// </summary>
[RegisterComponent]
public sealed partial class LLMPlayerComponent : Component
{
    /// <summary>
    ///     Conversation history for LLM context, stored as a list of (role, content) pairs.
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
