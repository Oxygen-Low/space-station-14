using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.GameTicking;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
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
///     queries an Ollama LLM for their actions including movement, speech, item pickup,
///     emotes, item use, and interactions based on species, health, and surroundings.
/// </summary>
public sealed class LLMPlayerSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly StationSpawningSystem _stationSpawning = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;

    private readonly OllamaService _ollama = new();

    private bool _enabled;
    private int _playerCount;
    private float _updateInterval;

    private const int SystemMessageCount = 2;
    private const int MaxConversationMessages = 22;
    private const int MaxMessages = SystemMessageCount + MaxConversationMessages;

    /// <summary>
    ///     Range in tiles to scan for nearby entities each turn.
    /// </summary>
    private const float NearbyRange = 5f;

    /// <summary>
    ///     Maximum number of nearby items to report to the LLM.
    /// </summary>
    private const int MaxNearbyItems = 8;

    /// <summary>
    ///     Maximum number of nearby people to report to the LLM.
    /// </summary>
    private const int MaxNearbyPeople = 6;

    private const string SystemPrompt =
        """
        You are a crew member aboard a space station. You are roleplaying as your character.
        Respond with a JSON object containing your next action. Available actions:
        - {"action": "say", "message": "<what to say>"} — speak in-character.
        - {"action": "emote", "type": "<emote>"} — perform an emote. Types: Laugh, Scream, Sigh, Crying, Clap, Snap, Salute, Gasp, Whistle.
        - {"action": "move", "direction": "<north|south|east|west>"} — walk in a direction.
        - {"action": "pickup", "target": "<item name>"} — pick up a nearby item from the ground.
        - {"action": "use"} — use/activate the item currently in your active hand.
        - {"action": "drop"} — drop the item in your active hand.
        - {"action": "interact", "target": "<entity name>"} — interact with/activate a nearby object or device.
        - {"action": "idle"} — do nothing this turn.

        Rules:
        - Always respond with ONLY a single JSON object, no extra text.
        - Stay in character. React to your health, species traits, and surroundings.
        - Keep messages short and natural (1-2 sentences max).
        - Pick up items that might be useful. Use items you're holding when appropriate.
        - React to injuries and nearby dangers. Interact with people and things around you.
        """;

    private readonly HashSet<Entity<ItemComponent>> _nearbyItemEntities = new();

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

            // Mark this entity as LLM-controlled and store character info
            var llmComp = EnsureComp<LLMPlayerComponent>(mob.Value);
            llmComp.CharacterName = profile.Name;
            llmComp.Species = profile.Species;
            llmComp.Age = profile.Age;
            llmComp.Gender = profile.Gender.ToString();
            llmComp.Job = "Passenger";

            llmComp.ConversationHistory.Add(new LLMMessage("system", SystemPrompt));
            llmComp.ConversationHistory.Add(new LLMMessage("system",
                BuildCharacterContext(llmComp)));

            Log.Info($"LLMPlayerSystem: Spawned LLM player '{profile.Name}' ({profile.Species}, {profile.Gender}, age {profile.Age}) on station {ToPrettyString(station)}.");
        }
    }

    /// <summary>
    ///     Builds a rich character context message including species, age, gender, and job info.
    /// </summary>
    private string BuildCharacterContext(LLMPlayerComponent llm)
    {
        var sb = new StringBuilder();
        sb.Append($"Your character's name is {llm.CharacterName}. ");
        sb.Append($"You are a {llm.Age}-year-old {llm.Gender} {llm.Species}. ");
        sb.Append($"Your role on the station is: {llm.Job}. ");

        // Add species-specific personality hints
        sb.Append(llm.Species.ToString() switch
        {
            "Reptilian" => "As a Reptilian, you have a proud and noble demeanor. You appreciate warmth and dislike the cold. ",
            "Dwarf" => "As a Dwarf, you are sturdy and love hard work. You have a fondness for craftsmanship and mining. ",
            "Moth" => "As a Moth (Nian), you are drawn to light and have a gentle, curious nature. You love fabrics and clothing. ",
            "Arachnid" => "As an Arachnid, you are patient and methodical. You can be unsettling to some but are highly perceptive. ",
            "SlimePerson" => "As a Slime Person, you are adaptable and resilient. You have a unique perspective on life and can be quite cheerful. ",
            "Diona" => "As a Diona, you are slow and thoughtful. You value nature and light, and speak in a deliberate manner. ",
            "Vox" => "As a Vox, you are shrewd and resourceful. You value your kin and can be cunning in your dealings. ",
            _ => "As a Human, you are adaptable and social. You get along with most species on the station. ",
        });

        sb.Append("Act naturally and interact with other crew members, using your items and surroundings.");
        return sb.ToString();
    }

    /// <summary>
    ///     Gathers situational awareness: health, held items, nearby items, and nearby people.
    /// </summary>
    private string BuildSituationPrompt(EntityUid uid, LLMPlayerComponent llm)
    {
        var sb = new StringBuilder();
        sb.AppendLine("It is your turn. Here is your current situation:");

        // Health status
        if (TryComp<MobStateComponent>(uid, out var mobStateComp))
        {
            var stateDesc = mobStateComp.CurrentState switch
            {
                MobState.Alive => "alive and functional",
                MobState.Critical => "in CRITICAL condition — you are dying and need medical help urgently",
                MobState.Dead => "DEAD",
                _ => "unknown",
            };
            sb.AppendLine($"- Health: You are {stateDesc}.");
        }

        if (TryComp<DamageableComponent>(uid, out var damageable))
        {
            var totalDmg = _damageable.GetTotalDamage((uid, damageable));
            if (totalDmg > 0)
            {
                var damagePerGroup = _damageable.GetDamagePerGroup((uid, damageable));
                sb.Append("- Injuries:");
                foreach (var (group, amount) in damagePerGroup)
                {
                    if (amount > 0)
                        sb.Append($" {group}: {amount}");
                }
                sb.AppendLine();
            }
        }

        // Items in hands
        if (TryComp<HandsComponent>(uid, out var handsComp))
        {
            var heldItems = new List<string>();
            foreach (var heldEntity in _hands.EnumerateHeld((uid, handsComp)))
            {
                heldItems.Add(Name(heldEntity));
            }

            if (heldItems.Count > 0)
                sb.AppendLine($"- Holding: {string.Join(", ", heldItems)}.");
            else
                sb.AppendLine("- Hands: empty.");
        }

        // Nearby items on the ground
        var xform = Transform(uid);
        _nearbyItemEntities.Clear();
        _lookup.GetEntitiesInRange(xform.Coordinates, NearbyRange, _nearbyItemEntities);

        var itemNames = new List<string>();
        foreach (var itemEnt in _nearbyItemEntities)
        {
            if (itemEnt.Owner == uid)
                continue;

            // Skip items that are already held by someone
            if (HasComp<HandsComponent>(Transform(itemEnt).ParentUid) && Transform(itemEnt).ParentUid != Transform(uid).MapUid)
                continue;

            var itemName = Name(itemEnt);
            if (!string.IsNullOrWhiteSpace(itemName) && !itemNames.Contains(itemName))
            {
                itemNames.Add(itemName);
                if (itemNames.Count >= MaxNearbyItems)
                    break;
            }
        }

        if (itemNames.Count > 0)
            sb.AppendLine($"- Nearby items: {string.Join(", ", itemNames)}.");

        // Nearby people
        var nearbyPeople = new List<string>();
        var mobQuery = EntityQueryEnumerator<HumanoidProfileComponent, MobStateComponent>();
        while (mobQuery.MoveNext(out var otherUid, out var profile, out var otherMobState))
        {
            if (otherUid == uid)
                continue;

            var otherXform = Transform(otherUid);
            if (otherXform.MapID != xform.MapID)
                continue;

            var distance = (otherXform.WorldPosition - xform.WorldPosition).Length();
            if (distance > NearbyRange)
                continue;

            var desc = Name(otherUid);
            if (otherMobState.CurrentState == MobState.Critical)
                desc += " (injured/dying)";
            else if (otherMobState.CurrentState == MobState.Dead)
                desc += " (dead)";

            nearbyPeople.Add(desc);
            if (nearbyPeople.Count >= MaxNearbyPeople)
                break;
        }

        if (nearbyPeople.Count > 0)
            sb.AppendLine($"- Nearby people: {string.Join(", ", nearbyPeople)}.");
        else
            sb.AppendLine("- You are alone.");

        sb.Append("Decide your next action.");
        return sb.ToString();
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

            // Skip dead entities
            if (_mobState.IsDead(uid))
                continue;

            llm.TimeSinceLastUpdate = 0f;
            llm.AwaitingResponse = true;

            // Build a rich situational prompt with awareness of surroundings
            var situationMessage = new LLMMessage("user", BuildSituationPrompt(uid, llm));
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

            // Trim conversation history to prevent unbounded growth (keep system + last N messages)
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
        if (llm.ConversationHistory.Count <= MaxMessages)
            return;

        // Keep system messages (first SystemMessageCount) and trim older conversation messages
        var systemMessages = llm.ConversationHistory.GetRange(0, SystemMessageCount);
        var recentMessages = llm.ConversationHistory.GetRange(
            llm.ConversationHistory.Count - MaxConversationMessages, MaxConversationMessages);

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
                    ExecuteSay(uid, root);
                    break;

                case "emote":
                    ExecuteEmote(uid, root);
                    break;

                case "move":
                    ExecuteMove(uid, root);
                    break;

                case "pickup":
                    ExecutePickup(uid, root);
                    break;

                case "use":
                    ExecuteUse(uid);
                    break;

                case "drop":
                    ExecuteDrop(uid);
                    break;

                case "interact":
                    ExecuteInteract(uid, root);
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

    private void ExecuteSay(EntityUid uid, System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msgProp))
            return;

        var message = msgProp.GetString();
        if (!string.IsNullOrWhiteSpace(message))
        {
            _chat.TrySendInGameICMessage(uid, message, InGameICChatType.Speak,
                ChatTransmitRange.Normal, ignoreActionBlocker: true);
        }
    }

    private void ExecuteEmote(EntityUid uid, System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("type", out var emoteProp))
            return;

        var emoteId = emoteProp.GetString();
        if (!string.IsNullOrWhiteSpace(emoteId))
        {
            _chat.TryEmoteWithChat(uid, emoteId, ignoreActionBlocker: true);
        }
    }

    private void ExecuteMove(EntityUid uid, System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("direction", out var dirProp))
            return;

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
            // MaxValue signals that this input applies to the entire tick,
            // matching the convention used by NPCSteeringSystem.
            mover.LastInputSubTick = ushort.MaxValue;
        }
    }

    private void ExecutePickup(EntityUid uid, System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("target", out var targetProp))
            return;

        var targetName = targetProp.GetString();
        if (string.IsNullOrWhiteSpace(targetName))
            return;

        // Find the target item nearby
        var xform = Transform(uid);
        _nearbyItemEntities.Clear();
        _lookup.GetEntitiesInRange(xform.Coordinates, NearbyRange, _nearbyItemEntities);

        var targetNameLower = targetName.ToLowerInvariant();
        foreach (var itemEnt in _nearbyItemEntities)
        {
            if (itemEnt.Owner == uid)
                continue;

            var itemName = Name(itemEnt);
            if (itemName.ToLowerInvariant().Contains(targetNameLower))
            {
                _hands.TryPickupAnyHand(uid, itemEnt, checkActionBlocker: false);
                Log.Debug($"LLMPlayerSystem: {ToPrettyString(uid)} picked up {itemName}.");
                return;
            }
        }

        Log.Debug($"LLMPlayerSystem: {ToPrettyString(uid)} could not find '{targetName}' nearby to pick up.");
    }

    private void ExecuteUse(EntityUid uid)
    {
        if (!_hands.TryGetActiveItem(uid, out var heldItem))
            return;

        _interaction.UseInHandInteraction(uid, heldItem.Value, checkCanUse: false, checkCanInteract: false);
        Log.Debug($"LLMPlayerSystem: {ToPrettyString(uid)} used {Name(heldItem.Value)}.");
    }

    private void ExecuteDrop(EntityUid uid)
    {
        _hands.TryDrop(uid, checkActionBlocker: false);
        Log.Debug($"LLMPlayerSystem: {ToPrettyString(uid)} dropped active hand item.");
    }

    private void ExecuteInteract(EntityUid uid, System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("target", out var targetProp))
            return;

        var targetName = targetProp.GetString();
        if (string.IsNullOrWhiteSpace(targetName))
            return;

        // Find the target entity nearby
        var xform = Transform(uid);
        var targetNameLower = targetName.ToLowerInvariant();

        // Search all nearby entities (not just items)
        _nearbyItemEntities.Clear();
        _lookup.GetEntitiesInRange(xform.Coordinates, NearbyRange, _nearbyItemEntities);

        // First check items, then fall back to broader search
        foreach (var itemEnt in _nearbyItemEntities)
        {
            if (itemEnt.Owner == uid)
                continue;

            var entityName = Name(itemEnt);
            if (entityName.ToLowerInvariant().Contains(targetNameLower))
            {
                _interaction.InteractionActivate(uid, itemEnt, checkCanInteract: false);
                Log.Debug($"LLMPlayerSystem: {ToPrettyString(uid)} interacted with {entityName}.");
                return;
            }
        }

        Log.Debug($"LLMPlayerSystem: {ToPrettyString(uid)} could not find '{targetName}' nearby to interact with.");
    }
}
