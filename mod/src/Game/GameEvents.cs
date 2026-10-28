using System.Collections.Generic;
using UnityEngine;

namespace Dolophobia.Game;

internal enum Audience { Ghost = 1, Intern = 2, Admin = 4, Everyone = Ghost | Intern | Admin, Truck = Intern | Admin }

internal sealed class GameEvent
{
    public string Kind;
    public Audience Audience;
    public Vector3? Position;
    public Dictionary<string, object> Data;
}

/// <summary>Collects events raised by Harmony hooks until the behaviour flushes them. Main thread only.</summary>
internal static class GameEvents
{
    public static readonly List<GameEvent> Pending = new();

    /// <summary>Latest truck sound-sensor readings by sensor index (SoundSensorData.UpdateSensorValue).</summary>
    public static readonly Dictionary<int, float> SoundSensorLevels = new();

    public static void Raise(string kind, Audience audience, Vector3? position = null, Dictionary<string, object> data = null)
    {
        // Hooks fire even in the lobby/main menu; there's nobody to tell and no level to locate them in.
        if (!DolophobiaBehaviour.Hosting) return;
        Pending.Add(new GameEvent { Kind = kind, Audience = audience, Position = position, Data = data });
    }
}
