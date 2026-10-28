# Wire protocol

All messages are JSON text frames with a `t` (type) field, except camera frames, which are binary.
Two WebSocket endpoints on the relay:

- `/host`: the BepInEx mod (one per lobby)
- `/play`: browsers

## Roles

| Role | Joins with | Receives | Can run |
| --- | --- | --- | --- |
| `ghost` | Ghost PIN (max 1 connected) | ghost view, ghost events | `ghost.event`, `ghost.hunt` (with cooldowns) |
| `intern` | Intern PIN (max `Auth.MaxInterns`) | intern view, truck events, camera frames | `cctv.watch` |
| `admin` | Admin PIN (the host's own console) | admin view, all events, camera frames | everything, no cooldowns |

PINs are printed in the BepInEx console when the lobby opens. They're random per launch unless fixed in config.

## Browser ⇄ relay (`/play`)

```jsonc
// → join (or resume after a page reload / host reconnect)
{ "t": "join",   "lobby": "K7M2Q", "role": "ghost", "pin": "8XKQ3D", "name": "Sam" }
{ "t": "resume", "lobby": "K7M2Q", "token": "<token from joined>" }

// ← result
{ "t": "joined", "lobby": "K7M2Q", "role": "ghost", "token": "…", "name": "Sam" }
{ "t": "join.failed", "error": "Wrong PIN." }

// → command (only after joined; the relay attaches your token for you)
{ "t": "cmd", "reqId": "any-string", "cmd": "ghost.event", "args": { "kind": "door" } }
// ← its result
{ "t": "cmd.result", "reqId": "any-string", "ok": false, "error": "Recovering... 6s" }

// ← pushed state
{ "t": "view",  "data": { … role-specific, see below … } }
{ "t": "event", "data": { "kind": "motion", "room": "Kitchen" } }
{ "t": "host.offline" } / { "t": "host.online" }   // relay auto-resumes your session when the host returns
```

Close codes: `4001` kicked by host, `4029` too many join attempts.

### Camera frames (binary, intern + admin only)

`[0x01][camera index: u8][JPEG bytes…]`. Frames are only rendered for cameras someone has selected
with `cctv.watch`. Each browser should ignore indices it isn't watching.

## Commands

| `cmd` | `args` | Notes |
| --- | --- | --- |
| `ghost.event` | `{ kind }` | `door`, `throw`, `light`, `flicker`, `fusebox`, `knock`, `window_knock`, `car_alarm`, `appear`, `interact`, `ability`, `random`, `whisper`. Ghost: `Ghost.EventCooldownSeconds`. |
| `ghost.hunt` | `{}` | Ghost: `Ghost.HuntCooldownSeconds`, disabled if `Ghost.CanForceHunt=false`. |
| `ghost.stop_hunt` | `{}` | Admin only. |
| `cctv.watch` | `{ index: number \| null }` | Start/stop receiving frames for a camera from the view's `cameras` list. |
| `sanity.set` | `{ actor, value: 0-100 }` | Admin. Host's own player only for now (owner-authoritative). |
| `session.kick` | `{ token }` | Admin. 8-char token prefix from the admin view's `sessions`. |

All roles: 10-command burst, refilling 3/s.

## Views (pushed at `Streaming.ViewHz`, default 10 Hz)

Outside an investigation, every role gets `{ "phase": "waiting", "reason": "…" }`.

**ghost** – ghost-relative and deliberately unreliable. No names, no exact positions.
```jsonc
{
  "phase": "investigation",
  "ghostType": "Banshee",        // you are the ghost; use it to pick sensitivities in the UI
  "hunting": false,
  "lightsOn": true,              // lights on in your room (light-sensitive ghosts)
  "talkingNearby": false,        // players talking in your room (sound-sensitive ghosts)
  "blips": [
    { "id": "a91f0c",            // stable per player for this game session, not their name
      "x": 3.5, "z": -6.0,        // metres, ghost-relative (+z = ghost's forward), noise grows with distance
      "level": 0,                // -1 below / 0 same floor / 1 above
      "intensity": 0.75,         // 1 = on top of you
      "moving": true, "running": false, "charging": false }
  ],
  "cooldowns": { "event": 0, "hunt": 87 }   // seconds; hunt = -1 when disabled
}
```
Far blips (>12 m) randomly drop out of individual snapshots, so they flicker.

**intern** – the truck.
```jsonc
{
  "phase": "investigation",
  "players": [{ "actor": 1, "name": "Alex", "sanity": 82, "alive": true }],
  "averageSanity": 82,
  "soundSensors": [{ "index": 0, "level": 0.12 }],
  "cameras": [{ "index": 0, "room": "Kitchen" }]
}
```

**admin** – ground truth: ghost type/state/room/position, every player's room/position/speed/sanity, sessions.

## Events

| `kind` | To | Extra |
| --- | --- | --- |
| `motion` | intern, admin | `room` |
| `paranormal_sound` | intern, admin | `sound`: `footstep` \| `throw` \| `whisper`, `room` |
| `emf` | intern, admin | `room` |
| `hunt` | everyone | `active` |
| `player_died` | everyone | `actor`, `room` |
| `player_charging` | ghost, admin | `distance`. A player is sprinting straight at the ghost. Fires at most every 6 s per player. Play the "They're coming for you" lines on this. |

## Mod ⇄ relay (`/host`)

The mod sends `host.hello {hostKey, secret, version}` first; the relay answers `host.welcome {lobby}`.
The same `hostKey` always gets the same lobby code while the lobby lives (10 min after everyone leaves).

Relay → mod: `auth.request {reqId, conn, role, pin, name}` or `{reqId, conn, token}` (resume),
`cmd {reqId, conn, token, cmd, args}`, `client.left {conn}`.

Mod → relay: `auth.result {reqId, conn, ok, token, role, name | error}`, `view {role, data}`,
`event {roles[], data}`, `cmd.result {reqId, conn, ok, data | error}`, `kick {conn}`, binary camera frames.
