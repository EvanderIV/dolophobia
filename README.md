# Dolophobia

A friend-group shenanigans mod for Phasmophobia. The lobby host runs a BepInEx mod that streams
the host's authoritative game state to a web app, where friends without the game play as:

- **The Intern** – stuck in the truck: everyone's sanity, motion/sound sensors, paranormal sounds, CCTV feeds.
- **The Ghost** – a fuzzy, unreliable radar of the people hunting you, and the power to fight back
  (doors, throws, knocks, flickers, hunts) on a cooldown.

```
 Phasmophobia (host)                         Relay (Node)                     Browsers
┌──────────────────────┐  outbound wss   ┌──────────────────┐   wss /play  ┌──────────────┐
│ BepInEx IL2CPP mod   │ ──────────────▶ │ lobbies, routing │ ◀──────────▶ │ ghost        │
│  · reads game state  │   /host         │ socket auth      │              │ intern(s)    │
│  · per-role views    │ ◀────────────── │ (no game rules)  │              │ admin (host) │
│  · runs commands     │   commands      └──────────────────┘              └──────────────┘
└──────────────────────┘
```

The host connects **out** to the relay, so nobody port-forwards. The mod is the only authority:
the relay authenticates sockets and routes, and every command is re-checked in the mod
against the session's role, rate limit and cooldowns. Each role only ever receives its own view, so
the ghost's fuzzed radar is fuzzed on the host and the exact positions never reach the ghost's browser.

| Path | What |
| --- | --- |
| [mod/](mod/) | BepInEx 6 (IL2CPP) plugin, C# / .NET 6 |
| [server/](server/) | Relay + dev console (`/`), Node 24, no build step |
| [docs/PROTOCOL.md](docs/PROTOCOL.md) | Wire protocol, roles and commands |
| [docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) | What the mod hooks, how obfuscation is handled, what to re-check after game updates |
| [tools/](tools/) | Cpp2IL dump + type inspection scripts |

## Setup (host)

1. **BepInEx 6 bleeding-edge, IL2CPP x64** (build 791 is known good) unzipped into the Phasmophobia folder.
   BepInEx 5 will *not* work. Phasmophobia is an IL2CPP build.
2. **Launch Phasmophobia once** and wait at the main menu. The first launch takes a few minutes
   while BepInEx generates `BepInEx/interop/*.dll`, which the mod compiles against. Quit.
3. **Install the .NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`). It builds the `net6.0` target fine.
4. Build. The DLL is copied into `BepInEx/plugins/Dolophobia/` automatically:
   ```powershell
   dotnet build mod -c Release
   # different Steam library?  dotnet build mod -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Phasmophobia"
   ```
5. Run the relay:
   ```powershell
   cd server; npm install; npm start          # http://localhost:4787
   ```
6. Launch the game. The BepInEx console prints the lobby code and the Ghost / Intern / Admin PINs.
   Open `http://localhost:4787`, enter the lobby + Admin PIN, and you should see the admin view.

### Playing with remote friends

The relay has to be reachable from their browsers. Pick one:

- **Quick:** keep it on your PC and expose it with a tunnel, e.g. `cloudflared tunnel --url http://localhost:4787`.
  Leave the mod's `Relay.Url` on `ws://localhost:4787/host`. Friends open the tunnel URL.
- **Proper:** deploy `server/` anywhere that runs Node 24 and WebSockets (Fly.io, Render, a VPS).
  Set `RELAY_HOST_SECRET` there, then set `Relay.Url = wss://<host>/host` and `Relay.Secret` in
  `BepInEx/config/dev.dolophobia.mod.cfg`.

Without `RELAY_HOST_SECRET`, anyone who finds your relay can open lobbies on it. That's harmless
for local dev, but set it on anything public.

## Ground rules

- **Private lobbies with friends who know the mod is on.** Hunts and events forced by the web ghost are
  real, host-authoritative game events. Everyone in the lobby experiences them.
- **Game updates will break things.** The game's private members are obfuscated and re-shuffled per
  release. The mod resolves them by shape at runtime to survive most updates, but expect to re-verify after
  patches. See [docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md).
- To uninstall BepInEx, delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt`,
  `BepInEx/` and `dotnet/` from the game folder, or just verify game files in Steam.

## Status

| Piece | State |
| --- | --- |
| Relay: lobbies, PIN auth handoff, role routing, reconnect/resume, kick, camera fan-out | Working, tested (`cd server; npm test`) |
| Mod: relay client, sessions/PINs/permissions/rate-limit/cooldowns | Written, **not yet compiled** (needs interop from step 2) |
| Mod: state (sanity, rooms, ghost type/state/room, positions), events, ghost commands, CCTV | Written against the real game dump, **needs in-game verification**, see the checklist in GAME-INTERNALS |
| Changing *other* players' sanity | Needs the mod on their client too (sanity is owner-authoritative). Planned. |
| Voice: intern comms, ghost talk box | Not started. Plan in GAME-INTERNALS. |
| The actual web app (ghost radar, intern truck, epilepsy warning) | Not started. `server/public/index.html` is a protocol dev console only. |
