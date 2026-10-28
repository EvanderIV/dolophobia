using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Photon.Pun;
using UnityEngine;
using Dolophobia.Game;
using Dolophobia.Net;

namespace Dolophobia;

/// <summary>
/// Main-thread driver: drains relay messages, ticks the world, pushes per-role views and events,
/// and streams watched cameras. Everything that touches Unity or game objects happens here.
/// </summary>
public class DolophobiaBehaviour : MonoBehaviour
{
    public DolophobiaBehaviour(IntPtr ptr) : base(ptr) { }

    /// <summary>True while we're the Photon master client of an active room, i.e. authoritative for the ghost.</summary>
    internal static bool Hosting;

    static readonly Role[] AllRoles = { Role.Ghost, Role.Intern, Role.Admin };

    RelayClient _relay;
    Sessions _sessions;
    readonly World _world = new();
    readonly Views _views = new();
    readonly CameraStreamer _cameras = new();
    float _nextView, _nextErrorLog;
    bool _wasHunting;

    void Start()
    {
        var cfg = Plugin.Settings;
        _sessions = new Sessions(cfg);
        _relay = new RelayClient(cfg.RelayUrl.Value, cfg.RelaySecret.Value, cfg.HostKey.Value);
        _relay.Start();
    }

    void Update()
    {
        var now = Time.unscaledTime;
        try
        {
            while (_relay.Inbox.TryDequeue(out var msg)) Handle(msg, now);

            Hosting = PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient;
            if (Hosting) _world.Tick(now, Time.unscaledDeltaTime);

            var hunting = Hosting && _world.InLevel && _world.Hunting;
            if (hunting != _wasHunting)
            {
                _wasHunting = hunting;
                GameEvents.Raise("hunt", Audience.Everyone, null, new() { ["active"] = hunting });
            }

            if (now >= _nextView)
            {
                _nextView = now + 1f / Mathf.Max(1f, Plugin.Settings.ViewHz.Value);
                PushViews(now);
            }
            FlushEvents();

            if (Hosting && _world.InLevel)
            {
                var watched = _sessions.Connected.Where(s => s.WatchCamera.HasValue).Select(s => s.WatchCamera.Value);
                _cameras.Tick(_world, now, watched, _relay.SendBinary);
            }
        }
        catch (Exception e)
        {
            GameEvents.Pending.Clear();
            if (now >= _nextErrorLog)
            {
                _nextErrorLog = now + 5f;
                Plugin.Logger.LogError($"[Dolophobia] {e}");
            }
        }
    }

    void OnDestroy()
    {
        _relay?.Dispose();
        _cameras.Dispose();
    }

    // ---- relay -> mod ----

    void Handle(JsonObject msg, float now)
    {
        switch ((string)msg["t"])
        {
            case "host.welcome":
                Plugin.Logger.LogMessage(
                    $"\n========== Dolophobia lobby {msg["lobby"]} ==========\n" +
                    $"  Ghost PIN:  {_sessions.GhostPin}\n" +
                    $"  Intern PIN: {_sessions.InternPin}\n" +
                    $"  Admin PIN:  {_sessions.AdminPin}   (keep this one to yourself)\n" +
                    "=================================================");
                break;

            case "auth.request":
                Authenticate(msg);
                break;

            case "cmd":
                Execute(msg, now);
                break;

            case "client.left":
                _sessions.Disconnected((string)msg["conn"]);
                break;

            case "relay.down":
                _sessions.DisconnectAll();
                break;

            case "error":
                Plugin.Logger.LogError($"[Relay] {msg["error"]}");
                break;
        }
    }

    void Authenticate(JsonObject msg)
    {
        var conn = (string)msg["conn"];
        try
        {
            var s = _sessions.Authenticate(conn, (string)msg["role"], (string)msg["pin"], (string)msg["name"], (string)msg["token"]);
            Plugin.Logger.LogInfo($"[Auth] {s.Name} joined as {s.Role}");
            _relay.Send(new { t = "auth.result", reqId = (string)msg["reqId"], conn, ok = true, token = s.Token, role = RoleName(s.Role), name = s.Name });
        }
        catch (AuthException e)
        {
            _relay.Send(new { t = "auth.result", reqId = (string)msg["reqId"], conn, ok = false, error = e.Message });
        }
    }

    void Execute(JsonObject msg, float now)
    {
        var conn = (string)msg["conn"];
        var command = (string)msg["cmd"];
        var args = msg["args"] as JsonObject ?? new JsonObject();
        object result = null;
        try
        {
            var s = _sessions.Authorize((string)msg["token"], conn, command, now);
            if ((command.StartsWith("ghost.") || command.StartsWith("sanity.")) && !(Hosting && _world.InLevel))
                throw new CommandException("Not in an investigation right now.");

            switch (command)
            {
                case "ghost.event":
                    _sessions.CheckGhostCooldown(s, command, now);
                    GameActions.GhostEvent(_world, (string)args["kind"]);
                    _sessions.StartGhostCooldown(s, command, now);
                    break;

                case "ghost.hunt":
                    if (s.Role == Role.Ghost && !Plugin.Settings.GhostCanHunt.Value) throw new CommandException("Hunting is disabled by the host.");
                    _sessions.CheckGhostCooldown(s, command, now);
                    GameActions.StartHunt(_world);
                    _sessions.StartGhostCooldown(s, command, now);
                    break;

                case "ghost.stop_hunt":
                    GameActions.StopHunt(_world);
                    break;

                case "sanity.set":
                    GameActions.SetSanity(_world, (int)args["actor"], (float)args["value"]);
                    break;

                case "cctv.watch":
                    s.WatchCamera = args["index"] is JsonValue v ? (int)v : null;
                    break;

                case "session.kick":
                    var kicked = _sessions.Kick((string)args["token"]);
                    if (kicked.Conn != null) _relay.Send(new { t = "kick", conn = kicked.Conn });
                    result = new { name = kicked.Name };
                    break;
            }
            _relay.Send(new { t = "cmd.result", reqId = (string)msg["reqId"], conn, ok = true, data = result });
        }
        catch (Exception e) when (e is AuthException or CommandException or FormatException or InvalidOperationException or ArgumentException)
        {
            _relay.Send(new { t = "cmd.result", reqId = (string)msg["reqId"], conn, ok = false, error = e.Message });
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[Cmd] {command}: {e}");
            _relay.Send(new { t = "cmd.result", reqId = (string)msg["reqId"], conn, ok = false, error = "The house resisted. (internal error, see host log)" });
        }
    }

    // ---- mod -> relay ----

    void PushViews(float now)
    {
        foreach (var role in AllRoles)
        {
            if (!_sessions.AnyConnected(role)) continue;
            object data;
            if (!PhotonNetwork.InRoom) data = Views.NotHosting("The host isn't in a game.");
            else if (!PhotonNetwork.IsMasterClient) data = Views.NotHosting("This Dolophobia install isn't the lobby host.");
            else if (!_world.InLevel) data = Views.NotHosting("Waiting in the lobby for the investigation to start.");
            else data = role switch
            {
                Role.Ghost => _views.Ghost(_world, now, _sessions),
                Role.Intern => _views.Intern(_world),
                _ => _views.Admin(_world, _sessions),
            };
            _relay.Send(new { t = "view", role = RoleName(role), data });
        }
    }

    void FlushEvents()
    {
        foreach (var e in GameEvents.Pending)
        {
            var data = e.Data ?? new Dictionary<string, object>();
            data["kind"] = e.Kind;
            if (e.Position is { } pos) data["room"] = World.RoomName(_world.RoomAt(pos));
            var roles = AllRoles.Where(r => (e.Audience & (Audience)(1 << (int)r)) != 0).Select(RoleName).ToArray();
            _relay.Send(new { t = "event", roles, data });
        }
        GameEvents.Pending.Clear();
    }

    static string RoleName(Role role) => role.ToString().ToLowerInvariant();
}
