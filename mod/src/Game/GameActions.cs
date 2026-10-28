using System;
using System.Collections.Generic;
using System.Reflection;

namespace Dolophobia.Game;

/// <summary>Things the web app can make happen. Host + main thread only; ghost AI is master-client authoritative.</summary>
internal static class GameActions
{
    /// <summary>Web-facing event names -> GhostAI state enum value names (see Shapes.GhostState).</summary>
    public static readonly IReadOnlyDictionary<string, string> GhostEvents = new Dictionary<string, string>
    {
        ["door"] = "door",
        ["throw"] = "throwing",
        ["light"] = "light",
        ["flicker"] = "flicker",
        ["fusebox"] = "fusebox",
        ["knock"] = "doorKnock",
        ["window_knock"] = "windowKnock",
        ["car_alarm"] = "carAlarm",
        ["appear"] = "appear",
        ["interact"] = "interact",
        ["ability"] = "GhostAbility",
        ["random"] = "randomEvent",
    };

    static MethodInfo _changeState;

    /// <summary>
    /// GhostAI.ChangeState(state, interactable = null, flag = false). The state enum's type name is
    /// obfuscated, so we go through reflection and parse the readable value name into it.
    /// </summary>
    public static void ChangeGhostState(World world, string stateName)
    {
        if (world.Ghost == null) throw new CommandException("No ghost in this level yet.");
        _changeState ??= typeof(GhostAI).GetMethod("ChangeState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         ?? throw new CommandException("GhostAI.ChangeState not found. Game update?");
        var args = _changeState.GetParameters();
        var state = Enum.Parse(args[0].ParameterType, stateName);
        var call = new object[args.Length];
        call[0] = state;
        for (var i = 1; i < args.Length; i++) call[i] = args[i].HasDefaultValue ? args[i].DefaultValue : null;
        _changeState.Invoke(world.Ghost, call);
    }

    public static void GhostEvent(World world, string kind)
    {
        if (kind == "whisper")
        {
            var interaction = world.Ghost == null ? null : Shapes.GhostAI_Interaction.Value?.ReadRef<GhostInteraction>(world.Ghost);
            if (interaction == null) throw new CommandException("No ghost in this level yet.");
            interaction.GhostWhisper();
            return;
        }
        if (!GhostEvents.TryGetValue(kind, out var state)) throw new CommandException($"Unknown ghost event '{kind}'.");
        ChangeGhostState(world, state);
    }

    public static void StartHunt(World world)
    {
        if (world.Hunting) throw new CommandException("Already hunting.");
        ChangeGhostState(world, "hunting");
    }

    public static void StopHunt(World world)
    {
        if (!world.Hunting) throw new CommandException("Not hunting.");
        world.Ghost.StartCoroutine(world.Ghost.StopGhostFromHunting());
    }

    /// <summary>
    /// Sanity is owner-authoritative in Phasmophobia: each client simulates its own and broadcasts it
    /// via PlayerSanity.NetworkedUpdatePlayerSanity. The host can therefore only set its *own* sanity.
    /// Setting other players' sanity needs Dolophobia installed on their client (planned: custom Photon event).
    /// </summary>
    public static void SetSanity(World world, int actor, float sanity)
    {
        var target = world.Players.Find(p => p.Actor == actor) ?? throw new CommandException($"No player with actor {actor}.");
        if (!target.IsLocal) throw new CommandException("Only the host's own sanity can be set from the host (owner-authoritative).");
        var playerSanity = Shapes.Player_Sanity.Value?.ReadRef<PlayerSanity>(target.Player);
        if (playerSanity == null || Shapes.PlayerSanity_Insanity.Value == null) throw new CommandException("Sanity field not resolved. Game update?");
        Shapes.PlayerSanity_Insanity.Value.Write(playerSanity, 100f - Math.Clamp(sanity, 0f, 100f));
        playerSanity.UpdatePlayerSanity();
    }
}

internal sealed class CommandException : Exception
{
    public CommandException(string message) : base(message) { }
}
