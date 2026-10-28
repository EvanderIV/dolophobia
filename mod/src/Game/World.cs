using System.Collections.Generic;
using System.Linq;
using Photon.Pun;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dolophobia.Game;

internal sealed class TrackedPlayer
{
    public Player Player;
    public int Actor;
    public string Name;
    public bool IsLocal;
    public Vector3 Position;
    public Vector3 Velocity;
    public float Sanity = 100f;
    public bool Dead;
    public LevelRoom Room;
}

/// <summary>Host-side view of the current level. Main thread only.</summary>
internal sealed class World
{
    const float RescanInterval = 2f;

    public GhostAI Ghost { get; private set; }
    public readonly List<TrackedPlayer> Players = new();
    public readonly List<CCTV> Cameras = new();
    LevelRoom[] _rooms = System.Array.Empty<LevelRoom>();
    float _nextRescan;

    /// <summary>Actors whose death RPC fired this level (set by <see cref="Patches"/>).</summary>
    public static readonly HashSet<int> DeadActors = new();

    public bool InLevel => Ghost != null;

    public void Tick(float now, float dt)
    {
        if (now >= _nextRescan || (Ghost == null && _rooms.Length > 0))
        {
            Rescan();
            _nextRescan = now + RescanInterval;
        }

        foreach (var p in Players)
        {
            if (p.Player == null) continue;
            var pos = p.Player.transform.position;
            if (dt > 0f) p.Velocity = Vector3.Lerp(p.Velocity, (pos - p.Position) / dt, 0.5f);
            p.Position = pos;
            p.Dead = DeadActors.Contains(p.Actor);
            p.Room = Shapes.Player_Room.Value?.ReadRef<LevelRoom>(p.Player);

            var sanity = Shapes.Player_Sanity.Value?.ReadRef<PlayerSanity>(p.Player);
            if (sanity != null && Shapes.PlayerSanity_Insanity.Value != null)
                p.Sanity = Shapes.SanityFromInsanity(Shapes.PlayerSanity_Insanity.Value.Read<float>(sanity));
        }
    }

    void Rescan()
    {
        var ghost = Object.FindObjectOfType<GhostAI>();
        if (ghost != Ghost)
        {
            DeadActors.Clear();
            Ghost = ghost;
        }

        var seen = new HashSet<int>();
        foreach (var player in Object.FindObjectsOfType<Player>())
        {
            var view = player.GetComponent<PhotonView>() ?? player.GetComponentInParent<PhotonView>();
            var actor = view?.Owner?.ActorNumber ?? player.GetInstanceID();
            seen.Add(actor);
            var tracked = Players.FirstOrDefault(p => p.Actor == actor);
            if (tracked == null)
            {
                tracked = new TrackedPlayer { Actor = actor, Position = player.transform.position };
                Players.Add(tracked);
            }
            tracked.Player = player;
            tracked.IsLocal = view == null || view.IsMine;
            tracked.Name = view?.Owner?.NickName ?? player.name;
        }
        Players.RemoveAll(p => !seen.Contains(p.Actor));

        Cameras.Clear();
        Cameras.AddRange(Object.FindObjectsOfType<CCTV>().OrderBy(c => c.transform.position.x).ThenBy(c => c.transform.position.z));

        _rooms = Object.FindObjectsOfType<LevelRoom>().ToArray();
    }

    public LevelRoom GhostRoom
    {
        get
        {
            var info = Ghost == null ? null : Shapes.GhostAI_Info.Value?.ReadRef<GhostInfo>(Ghost);
            return info == null ? null : Shapes.GhostInfo_Room.Value?.ReadRef<LevelRoom>(info);
        }
    }

    public string GhostType
    {
        get
        {
            var info = Ghost == null ? null : Shapes.GhostAI_Info.Value?.ReadRef<GhostInfo>(Ghost);
            if (info == null || Shapes.GhostInfo_Type.Value == null) return null;
            return Shapes.NameAt(Shapes.GhostTypeNames.Value, Shapes.GhostInfo_Type.Value.Read<int>(info));
        }
    }

    public string GhostState =>
        Ghost == null || Shapes.GhostAI_State.Value == null
            ? null
            : Shapes.NameAt(Shapes.GhostStateNames.Value, Shapes.GhostAI_State.Value.Read<int>(Ghost));

    public bool Hunting => GhostState == "hunting";

    public LevelRoom RoomAt(Vector3 position)
    {
        foreach (var room in _rooms)
        {
            if (room == null || room.roomColliders == null) continue;
            for (var i = 0; i < room.roomColliders.Count; i++)
            {
                var col = room.roomColliders[i];
                if (col != null && col.bounds.Contains(position)) return room;
            }
        }
        return null;
    }

    public static string RoomName(LevelRoom room) => room == null ? null : room.gameObject.name;

    public TrackedPlayer Local => Players.FirstOrDefault(p => p.IsLocal);
}
