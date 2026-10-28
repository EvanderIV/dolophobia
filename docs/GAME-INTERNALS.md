# Game internals

Notes on how the mod reaches into Phasmophobia, written against the build installed on 2026-10-28
(Unity 2022.3.40f1, IL2CPP metadata v31, BepInEx 6.0.0-be.791).

## What we're dealing with

- **IL2CPP.** There's no managed `Assembly-CSharp.dll` to decompile. BepInEx 6 generates C# *interop* stubs
  (`BepInEx/interop/`) on first launch, and those stubs call into native code. Harmony patches detour the native methods.
- **Photon PUN 2** for networking (`PhotonUnityNetworking.dll`), **Vivox** for voice.
  The **master client is authoritative for the ghost** (AI, events, hunts). That's why only the host needs the mod
  to read and drive the ghost.
- **Obfuscation (Beebyte-style).** Class names are readable (`GhostAI`, `PlayerSanity`, `CCTV`…), and so are
  public API methods, Unity messages (`Start`, `Update`) and every `[PunRPC]` method, since Photon dispatches
  RPCs by name. Everything else (private methods, almost all fields, enum *type* names, parameter names)
  is renamed to random Malayalam-script strings, padded with dozens of decoy methods per class, and **re-rolled each game update**.
  Enum *value* names are still readable (`hunting`, `Banshee`, `EMF`).

## How the mod copes

1. **Only patch readable names.** All Harmony targets in [Patches.cs](../mod/src/Game/Patches.cs) are public
   methods or `[PunRPC]`s. Parameters are bound positionally (`__0`, `__1`), never by obfuscated names.
2. **Resolve obfuscated fields by shape at runtime** ([Obf.cs](../mod/src/Game/Obf.cs), [Shapes.cs](../mod/src/Game/Shapes.cs)).
   For example "the only field on `Player` whose type is `PlayerSanity`", or "the enum field on `GhostAI` containing the value `hunting`".
   IL2CPP reflection finds the field. After that we read it at its native offset, exactly like interop accessors do.
   Field order and names can change and this still works. It only breaks if the *shape* changes, e.g. a second `LevelRoom` field on `GhostInfo`.
   Failures log `[Obf] Could not resolve …` once, and that feature degrades instead of crashing.
3. **Call obfuscated-typed methods through reflection.** `GhostAI.ChangeState(<obfuscated enum>, …)` is invoked with
   `Enum.Parse(paramType, "hunting")`, so we never name the enum type.

## Hook map

| Need | Source | Kind |
| --- | --- | --- |
| Ghost state / hunting | `GhostAI.state` (enum with `idle, wander, hunting, favouriteRoom, light, door, throwing, fusebox, appear, doorKnock, windowKnock, carAlarm, flicker, cctv, randomEvent, GhostAbility, mannequin, teleportObject, interact, summoningCircle, musicBox, dots, salt, ignite`) | shape |
| Force ghost events / hunts | `GhostAI.ChangeState(state, interact = null, bool = false)` | public method, via reflection |
| Stop a hunt | `GhostAI.StopGhostFromHunting()` coroutine | public method |
| Ghost whisper | `GhostInteraction.GhostWhisper()` | public method |
| Ghost type | `GhostInfo.<traits struct>.<type enum>` (Spirit … Deildegast) | shape (nested struct) |
| Ghost favourite room | `GhostInfo.<LevelRoom>` | shape |
| Player sanity | `Player.<PlayerSanity>` → `PlayerSanity.<only public float>` (insanity, 0-100) | shape |
| Player room | `Player.<LevelRoom>` | shape |
| Room for a world position | `LevelRoom.roomColliders` bounds | readable field |
| Lights / talking in ghost room | `LevelRoom.AreRoomLightsOn(out bool)`, `LevelRoom.ArePlayersTalkingInRoom(float)` | public methods |
| Motion sensor trips | `MotionSensorData.Detected(MotionSensor)` (truck display) | public method, patched |
| Sound sensor levels | `SoundSensorData.UpdateSensorValue(int, float)` | public method, patched |
| Footsteps / throws / whispers / EMF | `GhostInteraction.SpawnFootstepNetworked`, `PlayThrowingNoise`, `SpawnGhostWhisperNetworked`, `SpawnEMFNetworked` | `[PunRPC]`, patched |
| Deaths / revives | `Player.StartDeathAnimation` `[PunRPC]`, `Player.RevivePlayer` | patched |
| Player identity | `PhotonView.Owner.NickName / ActorNumber` on the `Player` object | Photon |
| CCTV | `Object.FindObjectsOfType<CCTV>()`, `CCTV.<Camera>` rendered to our own RenderTexture | shape |
| Am I host? | `PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient` | Photon |

## First-run verification checklist

None of the game-facing code has run yet. On the first real session, with the admin console open:

- [ ] Build succeeds against the generated interop. Likely snag: if BepInEx's interop **prefixes namespaces**
      (e.g. `Il2Cpp.GhostAI`) or exposes private members as non-public, fix the `using`s and names.
- [ ] BepInEx log has no `[Obf] Could not resolve` lines once in a level (set `LogLevels = All` in `BepInEx.cfg` to see the resolved offsets).
- [ ] Admin view: `ghost.type` matches the journal's real answer at the end of the round. `ghost.state` changes over time.
- [ ] Sanity **direction**: it should drop as you sit in the dark. If it rises, flip `Shapes.SanityFromInsanity`.
- [ ] Sanity for *remote* players updates (relies on Photon syncing their PlayerSanity to the host).
- [ ] Each `ghost.event` kind visibly does something. Some states may need an interactable and silently no-op. Note which.
- [ ] `ghost.hunt` starts a proper hunt (doors lock, lights flicker) for **all** players, and `ghost.stop_hunt` ends it.
- [ ] Motion / footstep / EMF events show up with sensible room names. `LevelRoom` GameObject names may need prettifying.
- [ ] `cctv.watch` streams a picture. If it's black, the URP camera may need `UniversalAdditionalCameraData` / post-processing
      handled. If frame time spikes, lower `Streaming.CameraFps` or resolution.
- [ ] Ghost-view blips line up with reality (compare to admin positions) and `charging` fires when someone sprints at the ghost.

## After a game update

```powershell
tools\dump-game.ps1                       # Cpp2IL dump into tools/.cache (not committed)
tools\members.ps1 GhostAI,PlayerSanity,GhostInfo,Player,CCTV,LevelRoom -Pattern '@0x|\('
```

Re-check the hook map above. Readable names rarely change. If a shape changed, adjust the matcher in `Shapes.cs`.
Also delete `BepInEx/interop` so BepInEx regenerates it, then rebuild the mod.

## Planned

- **Other players' sanity / per-player effects:** a small client-side mode of the same mod that listens for a custom
  Photon event (`PhotonNetwork.RaiseEvent`, picked up via a Harmony postfix on `LoadBalancingClient.OnEvent`) and
  applies it locally, since sanity is owner-authoritative.
- **Voice.** Vivox is closed and hard to inject into. The plan is to skip it:
  - *Intern comms:* WebRTC from the browser to the host, played in-game through an `AudioSource` attached to the
    host's walkie-talkie / truck radio (`TruckRadioController`, `WalkieTalkie` exist), so it sounds like radio chatter.
  - *Ghost talk box:* same transport, played from an `AudioSource` on the ghost with the game's paranormal audio mixer group,
    gated on ghost types that can speak.
  - *What the ghost hears:* muffled players needs a feed of in-game voice to the ghost's browser. Simplest is capturing
    the host's game audio near the ghost, but that's a bigger piece of work. Start with the ghost's UI reacting to
    `talkingNearby` and fake muffled audio.
