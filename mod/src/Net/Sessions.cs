using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Dolophobia.Net;

internal enum Role { Ghost, Intern, Admin }

internal sealed class Session
{
    public string Token;
    public Role Role;
    public string Name;
    public string Conn;       // relay connection id; null while the browser is disconnected
    public int? WatchCamera;  // interns/admin: CCTV index being streamed
    public double Tokens = Sessions.BurstSize;
    public float LastRefill;
}

/// <summary>
/// The mod is the authority on who may do what. The relay only authenticates sockets and routes;
/// every command is re-checked here against the session token the relay attaches.
/// </summary>
internal sealed class Sessions
{
    public const double BurstSize = 10;
    const double RefillPerSecond = 3;
    const int MaxPinFailuresPerConn = 5;

    /// <summary>Which roles may run which commands.</summary>
    static readonly Dictionary<string, Role[]> Permissions = new()
    {
        ["ghost.event"] = new[] { Role.Ghost, Role.Admin },
        ["ghost.hunt"] = new[] { Role.Ghost, Role.Admin },
        ["ghost.stop_hunt"] = new[] { Role.Admin },
        ["sanity.set"] = new[] { Role.Admin },
        ["cctv.watch"] = new[] { Role.Intern, Role.Admin },
        ["session.kick"] = new[] { Role.Admin },
    };

    public readonly string GhostPin, InternPin, AdminPin;

    readonly Dictionary<string, Session> _byToken = new();
    readonly Dictionary<string, int> _pinFailures = new();
    float _ghostEventReadyAt, _ghostHuntReadyAt;

    public Sessions(ModConfig cfg)
    {
        GhostPin = OrRandom(cfg.GhostPin.Value);
        InternPin = OrRandom(cfg.InternPin.Value);
        AdminPin = OrRandom(cfg.AdminPin.Value);
    }

    static string OrRandom(string pin) => string.IsNullOrWhiteSpace(pin) ? ModConfig.RandomPin() : pin.Trim().ToUpperInvariant();

    public IEnumerable<Session> Connected => _byToken.Values.Where(s => s.Conn != null);

    public bool AnyConnected(Role role) => Connected.Any(s => s.Role == role);

    /// <summary>Join with a PIN, or resume with a token previously issued by this host.</summary>
    public Session Authenticate(string conn, string roleName, string pin, string name, string resumeToken)
    {
        if (!string.IsNullOrEmpty(resumeToken))
        {
            if (!_byToken.TryGetValue(resumeToken, out var existing))
                throw new AuthException("Session expired. Rejoin with a PIN.");
            existing.Conn = conn;
            return existing;
        }

        if (_pinFailures.TryGetValue(conn, out var failures) && failures >= MaxPinFailuresPerConn)
            throw new AuthException("Too many wrong PINs.");

        if (!Enum.TryParse<Role>(roleName, true, out var role)) throw new AuthException($"Unknown role '{roleName}'.");
        if (PinRequired(role))
        {
            if (string.IsNullOrWhiteSpace(pin)) throw new AuthException("That role needs a PIN.");
            var expected = role switch { Role.Ghost => GhostPin, Role.Intern => InternPin, _ => AdminPin };
            if (!PinMatches(expected, pin))
            {
                _pinFailures[conn] = failures + 1;
                throw new AuthException("Wrong PIN.");
            }
        }

        if (role == Role.Ghost && AnyConnected(Role.Ghost)) throw new AuthException("Someone is already haunting this house.");
        if (role == Role.Intern && Connected.Count(s => s.Role == Role.Intern) >= Plugin.Settings.MaxInterns.Value)
            throw new AuthException("The truck is full.");

        var session = new Session
        {
            Token = ModConfig.RandomToken(24),
            Role = role,
            Name = string.IsNullOrWhiteSpace(name) ? role.ToString() : name.Trim()[..Math.Min(name.Trim().Length, 24)],
            Conn = conn,
        };
        _byToken[session.Token] = session;
        return session;
    }

    /// <summary>
    /// Admin sees the ghost's answers and can kick people, so it's always PIN-gated. Ghost/Intern only
    /// need the lobby code unless the host opts in, as in Phasmophobia's own room codes.
    /// </summary>
    public static bool PinRequired(Role role) => role == Role.Admin || Plugin.Settings.RequireRolePins.Value;

    static bool PinMatches(string expected, string given) =>
        given != null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given.Trim().ToUpperInvariant()));

    /// <summary>Validates token, permission, rate limit and role cooldowns. Returns the session or throws.</summary>
    public Session Authorize(string token, string conn, string command, float now)
    {
        if (token == null || !_byToken.TryGetValue(token, out var s) || s.Conn != conn)
            throw new AuthException("Not authenticated.");
        if (!Permissions.TryGetValue(command, out var roles)) throw new AuthException($"Unknown command '{command}'.");
        if (!roles.Contains(s.Role)) throw new AuthException($"{s.Role} can't do '{command}'.");

        s.Tokens = Math.Min(BurstSize, s.Tokens + (now - s.LastRefill) * RefillPerSecond);
        s.LastRefill = now;
        if (s.Tokens < 1) throw new AuthException("Slow down.");
        s.Tokens -= 1;
        return s;
    }

    /// <summary>Ghost-role cooldowns. Admin bypasses them.</summary>
    public void CheckGhostCooldown(Session s, string command, float now)
    {
        if (s.Role != Role.Ghost) return;
        var readyAt = command == "ghost.hunt" ? _ghostHuntReadyAt : _ghostEventReadyAt;
        if (now < readyAt) throw new AuthException($"Recovering... {readyAt - now:0}s");
    }

    /// <summary>Call only after the action succeeded, so a failed event doesn't burn the cooldown.</summary>
    public void StartGhostCooldown(Session s, string command, float now)
    {
        if (s.Role != Role.Ghost) return;
        if (command == "ghost.hunt") _ghostHuntReadyAt = now + Plugin.Settings.GhostHuntCooldown.Value;
        _ghostEventReadyAt = now + Plugin.Settings.GhostEventCooldown.Value;
    }

    public object GhostCooldowns(float now) => new
    {
        @event = Math.Max(0, (int)Math.Ceiling(_ghostEventReadyAt - now)),
        hunt = Plugin.Settings.GhostCanHunt.Value ? Math.Max(0, (int)Math.Ceiling(_ghostHuntReadyAt - now)) : -1,
    };

    public void Disconnected(string conn)
    {
        foreach (var s in _byToken.Values.Where(s => s.Conn == conn)) s.Conn = null;
        _pinFailures.Remove(conn);
    }

    /// <summary>Host lost the relay: every browser must re-resume once it's back.</summary>
    public void DisconnectAll()
    {
        foreach (var s in _byToken.Values) s.Conn = null;
        _pinFailures.Clear();
    }

    /// <summary>Kick by the 8-char token prefix shown in the admin view.</summary>
    public Session Kick(string tokenPrefix)
    {
        var s = tokenPrefix is { Length: >= 8 }
            ? _byToken.Values.FirstOrDefault(x => x.Token.StartsWith(tokenPrefix, StringComparison.Ordinal))
            : null;
        if (s == null) throw new AuthException("No such session.");
        _byToken.Remove(s.Token);
        return s;
    }

    public IEnumerable<object> Describe() => _byToken.Values.Select(s => new
    {
        token = s.Token[..8], role = s.Role.ToString().ToLowerInvariant(), name = s.Name, online = s.Conn != null,
    });
}

internal sealed class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
}
