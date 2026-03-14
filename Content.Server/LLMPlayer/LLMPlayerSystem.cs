using System.Numerics;
using System.Threading.Tasks;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Movement.Components;
using Content.Shared.Preferences;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.LLMPlayer;

/// <summary>
///     System that spawns and manages LLM-controlled players.
///     On round start, it spawns humanoid entities on the station and periodically
///     queries an Ollama LLM for their actions (movement and speech).
/// </summary>
public sealed class LLMPlayerSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly StationSpawningSystem _stationSpawning = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;

    private readonly OllamaService _ollama = new();

    private bool _enabled;
    private int _playerCount;
    private float _updateInterval;

    private const string SystemPrompt =
        """
        You are a crew member aboard a space station. You are roleplaying as your character.
        Respond with a JSON object containing your next action. Available actions:
        - {"action": "say", "message": "<what to say>"} — speak in-character.
        - {"action": "move", "direction": "<north|south|east|west>"} — walk in a direction.
        - {"action": "idle"} — do nothing this turn.

        Rules:
        - Always respond with ONLY a single JSON object, no extra text.
        - Stay in character. Be creative and interact with others.
        - Keep messages short and natural (1-2 sentences max).
        """;

    public override void Initialize()
    {
        base.Initialize();

        _ollama.Initialize();

        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);

        _cfg.OnValueChanged(CCVars.LLMPlayerEnabled, v => _enabled = v, true);
        _cfg.OnValueChanged(CCVars.LLMPlayerCount, v => _playerCount = v, true);
        _cfg.OnValueChanged(CCVars.LLMPlayerUpdateInterval, v => _updateInterval = v, true);
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (!_enabled)
            return;

        if (ev.New == GameRunLevel.InRound)
        {
            SpawnLLMPlayers();
        }
    }

    private void SpawnLLMPlayers()
    {
        // Find spawnable stations
        var stations = new List<EntityUid>();
        var query = EntityQueryEnumerator<StationJobsComponent, StationSpawningComponent>();
        while (query.MoveNext(out var uid, out _, out _))
        {
            stations.Add(uid);
        }

        if (stations.Count == 0)
        {
            Log.Warning("LLMPlayerSystem: No spawnable stations found, cannot spawn LLM players.");
            return;
        }

        for (var i = 0; i < _playerCount; i++)
        {
            var station = _random.Pick(stations);
            var profile = HumanoidCharacterProfile.Random();

            // Spawn the humanoid entity on the station
            var mob = _stationSpawning.SpawnPlayerCharacterOnStation(station, "Passenger", profile);

            if (mob == null)
            {
                Log.Warning("LLMPlayerSystem: Failed to spawn LLM player entity.");
                continue;
            }

            // Create a mind for this entity (no player session)
            var mindId = _mind.CreateMind(null, profile.Name);
            _mind.TransferTo(mindId, mob.Value);

            // Mark this entity as LLM-controlled
            var llmComp = EnsureComp<LLMPlayerComponent>(mob.Value);
            llmComp.ConversationHistory.Add(new LLMMessage("system", SystemPrompt));
            llmComp.ConversationHistory.Add(new LLMMessage("system",
                $"Your character's name is {profile.Name}. You are a Passenger on the station. Act naturally and interact with other crew members."));

            Log.Info($"LLMPlayerSystem: Spawned LLM player '{profile.Name}' on station {ToPrettyString(station)}.");
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_enabled)
            return;

        var enumerator = EntityQueryEnumerator<LLMPlayerComponent>();
        while (enumerator.MoveNext(out var uid, out var llm))
        {
            llm.TimeSinceLastUpdate += frameTime;

            if (llm.TimeSinceLastUpdate < _updateInterval || llm.AwaitingResponse)
                continue;

            llm.TimeSinceLastUpdate = 0f;
            llm.AwaitingResponse = true;

            // Add a situational prompt to give the LLM some context about this turn
            var situationMessage = new LLMMessage("user", "It is your turn. Decide your next action.");
            llm.ConversationHistory.Add(situationMessage);

            // Fire off the async LLM query
            var entityUid = uid;
            _ = QueryLLMAsync(entityUid, llm);
        }
    }

    private async Task QueryLLMAsync(EntityUid uid, LLMPlayerComponent llm)
    {
        try
        {
            var response = await _ollama.ChatAsync(llm.ConversationHistory);

            if (response == null)
            {
                llm.AwaitingResponse = false;
                return;
            }

            // Add assistant response to history
            llm.ConversationHistory.Add(new LLMMessage("assistant", response));

            // Trim conversation history to prevent unbounded growth (keep system + last 20 messages)
            TrimHistory(llm);

            // Parse and execute the action
            ExecuteAction(uid, response);
        }
        catch (Exception e)
        {
            Log.Error($"LLMPlayerSystem: Error querying LLM for {ToPrettyString(uid)}: {e}");
        }
        finally
        {
            llm.AwaitingResponse = false;
        }
    }

    private void TrimHistory(LLMPlayerComponent llm)
    {
        const int maxMessages = 24; // 2 system + up to 22 conversation messages
        if (llm.ConversationHistory.Count <= maxMessages)
            return;

        // Keep system messages (first 2) and trim older conversation messages
        var systemMessages = llm.ConversationHistory.GetRange(0, 2);
        var recentMessages = llm.ConversationHistory.GetRange(
            llm.ConversationHistory.Count - (maxMessages - 2), maxMessages - 2);

        llm.ConversationHistory.Clear();
        llm.ConversationHistory.AddRange(systemMessages);
        llm.ConversationHistory.AddRange(recentMessages);
    }

    private void ExecuteAction(EntityUid uid, string response)
    {
        // Strip markdown code fences if present
        var trimmed = response.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
                trimmed = trimmed[(firstNewline + 1)..];

            if (trimmed.EndsWith("```"))
                trimmed = trimmed[..^3];

            trimmed = trimmed.Trim();
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            if (!root.TryGetProperty("action", out var actionProp))
                return;

            var action = actionProp.GetString();
            switch (action)
            {
                case "say":
                    if (root.TryGetProperty("message", out var msgProp))
                    {
                        var message = msgProp.GetString();
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            _chat.TrySendInGameICMessage(uid, message, InGameICChatType.Speak,
                                ChatTransmitRange.Normal, ignoreActionBlocker: true);
                        }
                    }
                    break;

                case "move":
                    if (root.TryGetProperty("direction", out var dirProp))
                    {
                        var dir = dirProp.GetString()?.ToLowerInvariant();
                        var moveDir = dir switch
                        {
                            "north" => new Vector2(0, 1),
                            "south" => new Vector2(0, -1),
                            "east" => new Vector2(1, 0),
                            "west" => new Vector2(-1, 0),
                            _ => Vector2.Zero,
                        };

                        if (moveDir != Vector2.Zero && TryComp<InputMoverComponent>(uid, out var mover))
                        {
                            mover.CurTickSprintMovement = moveDir;
                            mover.LastInputTick = _timing.CurTick;
                            mover.LastInputSubTick = ushort.MaxValue;
                        }
                    }
                    break;

                case "idle":
                    break;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // LLM didn't return valid JSON — just treat as idle
            Log.Debug($"LLMPlayerSystem: Could not parse LLM response as JSON for {ToPrettyString(uid)}: {trimmed}");
        }
    }
}
