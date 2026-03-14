using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    ///     Whether LLM-controlled players are enabled.
    /// </summary>
    public static readonly CVarDef<bool> LLMPlayerEnabled =
        CVarDef.Create("llm.enabled", true, CVar.SERVERONLY);

    /// <summary>
    ///     The base URL for the Ollama API.
    /// </summary>
    public static readonly CVarDef<string> LLMPlayerOllamaUrl =
        CVarDef.Create("llm.ollama_url", "http://localhost:11434", CVar.SERVERONLY);

    /// <summary>
    ///     The Ollama model to use for LLM players.
    /// </summary>
    public static readonly CVarDef<string> LLMPlayerModel =
        CVarDef.Create("llm.model", "gemma3:12b", CVar.SERVERONLY);

    /// <summary>
    ///     Number of LLM-controlled players to spawn each round.
    /// </summary>
    public static readonly CVarDef<int> LLMPlayerCount =
        CVarDef.Create("llm.player_count", 2, CVar.SERVERONLY);

    /// <summary>
    ///     Interval in seconds between LLM decision updates.
    /// </summary>
    public static readonly CVarDef<float> LLMPlayerUpdateInterval =
        CVarDef.Create("llm.update_interval", 10f, CVar.SERVERONLY);
}
