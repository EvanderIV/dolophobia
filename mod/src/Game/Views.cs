using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Dolophobia.Net;

namespace Dolophobia.Game;

/// <summary>
/// Builds the per-role snapshots pushed to the relay. Each role only ever receives its own view,
/// so anything a role shouldn't know is simply never sent.
/// </summary>
internal sealed class Views
{
    const float WalkSpeed = 1.2f;
    const float RunSpeed = 2.6f;
    const float ChargeCooldown = 6f;

    readonly System.Random _rng = new();
    readonly Dictionary<int, float> _lastCharge = new();

    public static object NotHosting(string reason) => new { phase = "waiting", reason };

    /// <summary>
    /// What the ghost "senses": fuzzy, ghost-relative blips with no names. Noise grows with distance
    /// and far blips randomly drop out, so the radar feels unreliable unless someone is close.
    /// </summary>
    public object Ghost(World world, float now, Sessions sessions)
    {
        var ghost = world.Ghost.transform;
        var room = world.GhostRoom;
        var lightsOn = room != null && room.AreRoomLightsOn(out _);
        var talkingNearby = room != null && room.ArePlayersTalkingInRoom(0.1f);

        var blips = new List<object>();
        foreach (var p in world.Players)
        {
            if (p.Dead || p.Player == null) continue;
            var rel = ghost.InverseTransformDirection(p.Position - ghost.position);
            var dist = new Vector2(rel.x, rel.z).magnitude;
            if (dist > 12f && _rng.NextDouble() < Math.Min(0.6, (dist - 12f) / 20f)) continue;

            var sigma = 0.4f + dist * 0.12f;
            var speed = new Vector2(p.Velocity.x, p.Velocity.z).magnitude;
            var towardGhost = Vector3.Dot(p.Velocity.normalized, (ghost.position - p.Position).normalized);
            var charging = speed > RunSpeed && towardGhost > 0.7f && dist < 20f;

            if (charging && (!_lastCharge.TryGetValue(p.Actor, out var last) || now - last > ChargeCooldown))
            {
                _lastCharge[p.Actor] = now;
                GameEvents.Raise("player_charging", Audience.Ghost | Audience.Admin, null,
                    new() { ["distance"] = Round(dist, 1) });
            }

            blips.Add(new
            {
                id = BlipId(p.Actor),
                x = Round(rel.x + Gauss() * sigma, 0.5f),
                z = Round(rel.z + Gauss() * sigma, 0.5f),
                level = Math.Sign(Mathf.RoundToInt(rel.y / 2.5f)), // -1 below, 0 same floor, 1 above
                intensity = Round(Mathf.Clamp01(1f - dist / 25f), 0.05f),
                moving = speed > WalkSpeed,
                running = speed > RunSpeed,
                charging,
            });
        }

        return new
        {
            phase = "investigation",
            ghostType = world.GhostType,
            hunting = world.Hunting,
            lightsOn,
            talkingNearby,
            blips,
            cooldowns = sessions.GhostCooldowns(now),
        };
    }

    /// <summary>The truck: sanity, sensors, and the camera list. No ghost internals.</summary>
    public object Intern(World world)
    {
        var alive = world.Players.Where(p => !p.Dead).ToList();
        return new
        {
            phase = "investigation",
            players = world.Players.Select(p => new { actor = p.Actor, name = p.Name, sanity = Mathf.RoundToInt(p.Sanity), alive = !p.Dead }),
            averageSanity = alive.Count == 0 ? 0 : Mathf.RoundToInt(alive.Average(p => p.Sanity)),
            soundSensors = GameEvents.SoundSensorLevels.OrderBy(kv => kv.Key).Select(kv => new { index = kv.Key, level = Round(kv.Value, 0.01f) }),
            cameras = world.Cameras.Select((c, i) => new { index = i, room = World.RoomName(world.RoomAt(c.transform.position)) }),
        };
    }

    /// <summary>Host's own console: ground truth for debugging and game-mastering.</summary>
    public object Admin(World world, Sessions sessions)
    {
        var ghost = world.Ghost;
        return new
        {
            phase = "investigation",
            ghost = new
            {
                type = world.GhostType,
                state = world.GhostState,
                room = World.RoomName(world.GhostRoom),
                position = Vec(ghost.transform.position),
            },
            players = world.Players.Select(p => new
            {
                actor = p.Actor, name = p.Name, isHost = p.IsLocal, alive = !p.Dead,
                sanity = Mathf.RoundToInt(p.Sanity), room = World.RoomName(p.Room),
                position = Vec(p.Position), speed = Round(p.Velocity.magnitude, 0.1f),
            }),
            sessions = sessions.Describe(),
            cameras = world.Cameras.Count,
        };
    }

    public static float[] Vec(Vector3 v) => new[] { Round(v.x, 0.01f), Round(v.y, 0.01f), Round(v.z, 0.01f) };

    static float Round(float v, float step) => Mathf.Round(v / step) * step;

    // HashCode is randomly seeded per process, so ids are stable for a game session but don't leak actor numbers.
    static string BlipId(int actor) =>
        Convert.ToHexString(BitConverter.GetBytes(HashCode.Combine(actor)))[..6].ToLowerInvariant();

    float Gauss()
    {
        // Box-Muller
        var u1 = 1.0 - _rng.NextDouble();
        var u2 = _rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }
}
