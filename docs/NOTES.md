# POOLS (Tensori) — co-op working notes

Started 2026-11-07. Status: **flat (non-VR) build: a host and a client (two separate game processes, bots instead of people) connect over UDP and over real Steam P2P (one account), see each other as puppets drawn from an interpolation buffer, share hinged doors and ducks with epoch-based ownership, and invite through an F7 panel and Steam rich presence. VR build: same code, headless runs only. Not yet: a test with two different Steam accounts or two real people, real VR poses, a VR panel, chapter follow, chairs.**
Nothing here is a claim that co-op works. Path 2 (build from zero): no prior co-op mod found.

## Game identity (Phase 0)

| Field | Value |
|---|---|
| Title / developer | POOLS / Tensori (UNIKAT Label publisher) |
| Store | Steam, app id 2663530 (also ships EOS and GOG Galaxy code) |
| Steam build id | 21259568 |
| Engine | IL2CPP, global-metadata v31 (read from the VR build). **Unity 6000.0.61f1 in the flat build, Unity 6000.1.17f1 in the VR build** (`UnityPlayer.dll` version info of each, and the BepInEx log of the VR build). Both HDRP and URP assemblies ship; the VR build has URP-named types and a `urpVRBuildApplicationPath`, so its pipeline is probably URP (unverified) |
| OS tested | Windows 11 |
| Builds in the install | flat build `POOLS.exe` and a separate VR build `POOLSVR\POOLS.exe` (OpenXR) |
| SHA-256 `POOLS.exe` (flat) | `3455ADFF0B4E90F27EDF9FBBB34529E860F6B31198DDA7DCDEBB5666D192B29D` |
| SHA-256 `POOLSVR\POOLS.exe` | `A91594B9D86DFE50EF4D3F4C64AECB1B2417CF2F9FEF8E26B178D25C37C3BFE0` |
| SHA-256 `POOLSVR\GameAssembly.dll` | `C4CDD5CE44CFCB8689CCD9FCB16FDB0E8DB8318DB1EE125D2C6B200D52654B5B` |
| SHA-256 flat `GameAssembly.dll` | `108D5BE16909015A910E139503825A29174D0BC1829891CC3C075C7ECFFDFC50` |

The two builds have different `GameAssembly.dll` hashes, so a mod built against one must be re-checked on the other.

## Observations (things that were measured)

- No existing co-op mod: not in `DIRECTORY.md`, no issue in this repo, no hit on GitHub/Thunderstore/Nexus in a web search. Discord not searched yet.
- `POOLSVR` is a full ~5.9 GB self-contained copy of the game (119 files). Copied to a test folder; byte count and hashes of exe and `GameAssembly.dll` match the source.
- `boot.config` has `single-instance=` empty: nothing prevents running two copies.
- BepInEx 6 bleeding edge build 793 (`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793+dbc82f0.zip`, SHA-256 `5b63d7d0e92a2448ccd13b738ff95ff1cc1026c6d718555d4df8e6c824953ac4`, computed by us; the builds page publishes no hash) boots on this game. First launch with `-batchmode -nographics` generated interop assemblies and printed `Chainloader startup complete`. IL unstrip: 11884 ok, 1570 failed; methods restored 19422, failed 1224.
- The game saves to its persistent data folder (`chapter` save files plus Steam Auto-Cloud), which is shared by every copy of the game on the PC, not per install folder. Saves were backed up before the first test run and were byte-identical after it.
- Interop `Assembly-CSharp.dll` has 565 top-level types. **No type name matches network, netcode, mirror, photon, fishnet, lobby, multiplayer or rpc.** The game has no netcode of its own to reuse.
- Shipped helpers: `Facepunch.Steamworks.Win64.dll`, `steam_api64.dll` (VR build), `Newtonsoft.Json.dll`.
- Toolchain: portable .NET SDK 8.0.427 (zip SHA-512 matched the official release metadata) extracted in the workspace, no system install. BepInEx IL2CPP runs plugins on the .NET 6 runtime shipped in `<game dir>\dotnet` (CoreLib 6.0.722). The plugin is compiled offline against those exact assemblies (no targeting pack, no NuGet download); it built with 0 warnings.
- Probe plugin run (VR build, `-batchmode -nographics`, no headset, 70 s): the plugin loaded, its Harmony prefix on `SceneLoader.Load` applied, and `SceneManager.sceneLoaded` fired for `loadMainMenu` (build index 0) then `MainMenu_VR` (build index 1). In `MainMenu_VR` these singletons exist: `PoolsVRManager` (`IsVRModeActive` = True even with no headset), `PoolsXROrigin`, `FPSController`, `LevelManager`. Head and both hand transforms reported one identical pose (no tracking), so real pose data is still unseen.
- `VRModeSwitcher.urpVRBuildApplicationPath` and `flatScreenBuildApplicationPath` are empty strings at runtime in the test copy. `ToVRMode` / `ToFlatScreenMode` exist, so the game probably relaunches the other build to switch mode; this was not exercised. Only one game process ran; no process started from outside the test folder.
- Log lines print decimals with a comma on a Russian-locale PC; plugin code must format with `InvariantCulture` before anything is sent over the network.
- OpenXR cannot create an instance on this PC (no runtime/headset): `BepInEx\ErrorLog.log` fills with `xrCreateInstance failed` lines. The game still reaches `MainMenu_VR` and `PoolsVRManager.IsVRModeActive` is True, so that flag does not mean a headset works.
- Net layer (`PoolsCoop\Net`, pure .NET, no Unity types): star topology, one host and up to three guests; `Hello/Welcome/Pose/State/Ping/Pong/Bye` over UDP datagrams; poses are unreliable and newest-wins by 16-bit sequence; the host re-sends `State` (scene + player list) every second so there is no reliability layer; 5 s timeout; junk senders are forgotten. The transport is an interface (`ITransport`), so a Steam transport can replace UDP without touching the session. The plugin does nothing unless `[Net] Role` is set to `Host` or `Client`; the host binds `127.0.0.1` by default.
- Offline tests (`PoolsCoop.Tests`, `dotnet run`): 131 checks, `ALL OK`. They cover wire round trip, truncation, handshake, relay between two guests, 30% loss + 20% duplicates + 250 ms jitter, scene sync at 50% loss, crash/timeout/restart, capacity, a client forging another player's pose, 30000 fuzzed packets, real UDP sockets on loopback with a junk-spraying stranger, and the Steam transport against a fake Steam under three Accept behaviours (lenient, only-with-pending-request, send-first-only), with chaos, strangers, Steam-not-ready, junk and client restart. Deliberate bugs (accept stale poses; relay under the wrong player id; host never sends first; accept strangers; no sender-claim check) each made tests fail, so the tests can fail.
- Steam binding: the game's `steam_api64.dll` (file version 5.69.73.98) exports the flat `SteamAPI_SteamUser_v020`, `SteamFriends_v017`, `SteamNetworking_v006`, the old `SteamAPI_ISteamNetworking_*` P2P calls and `ISteamNetworkingSockets` v008, but **no** `SteamNetworkingMessages`. The plugin calls the old P2P API by `DllImport` (resolver loads the DLL from `<game>_Data\Plugins\x86_64`). It becomes ready a few seconds after launch, once the game has initialised Steam and the user is logged on. Self probe (one process, sends only to its own ID): `SendP2PPacket` to own ID returns true, session state goes `connecting` -> `active`, nothing comes back to the sender.
- Same-account lab run over real Steam (host and client are two processes of one account, each allowing/hosting the account's own ID): handshake, scene sync and about 650 poses per side arrived over Steam and matched the bot pose exactly, 0 malformed. The host additionally received echoes of its own packets (about 210 per run), which the host's sender-claim check drops; they are counted as `rejectedClaims`, and well-formed packets from connections outside the session as `strayPackets`. This proves the binding and the protocol over real Steam P2P between processes; it does not prove two different accounts can reach each other.
- Survey of the scenes (plugin `[Test] Recon`, build scenes: `loadMainMenu`, `MainMenu_VR` or `MainMenu`, `LoadingScene`, chapters `0`..`6` as scenes `0`,`1`..`6`, `Ending_VR`/`Ending`): non-kinematic rigid bodies per chapter are few (chapter 0: 119 of 214, chapter 1: 14 of 42, chapter 6: 52 of 139) and are almost all `XRSimpleInteractable` triggers on furniture, hinged `Door` bodies (`Door` + `HingeJoint`, 25 in chapter 0, 7 in chapter 6), and `BuoyancyObject` rubber ducks (3 in chapter 0, inactive until some event). Chapter 0 also has 134 `Autohand.Grabbable` (mostly climb handles and door handle children), 84 `Chair`, 25 `WaterBodyVolume`. There is no pile of loose props, so the sync covers doors and ducks.
- The flat build (Unity 6000.0.61f1, HDRP) has the same gameplay types as the VR build (Unity 6000.1.17f1, URP types only there) and the same plugin DLL runs on both. Its menu scene is `MainMenu` (a screen-space `MenuCanvas`), the VR one `MainMenu_VR`. The flat build opens a visible window even with `-batchmode -nographics`, so the test runner hides test windows with `ShowWindow`.
- Player sync: poses at 60 Hz (config `PoseHz`); the receiver draws each remote player from a snapshot buffer (`PoseBuffer`) that estimates the sender-clock offset as the minimum of (arrival - sender time) over 2 s, adapts its delay to the 90th percentile of the jitter, extrapolates up to 120 ms, and after a sender stall of more than 12 cm fades the jump out over about 200 ms (a 3 m or larger jump, i.e. a VR teleport, is shown at once). Simulation against the bot's analytic path (90 Hz drawing): good network (20 ms, 10 ms jitter): error 0.1 mm, staleness p95 55 ms; wifi-like (35 ms, 40 ms jitter, 5% loss): 0.5 mm, p95 105 ms; bad (60 ms, 120 ms jitter, 30% loss, 20% duplicates): 1.6 mm, p95 230 ms; no pop after a 600 ms sender freeze (max step 26 mm). In the real game, two flat-build processes over loopback UDP: drawn puppet within 0.0 mm (median) and 0.1 to 0.2 mm (95th percentile) of the true bot path, buffer delay 35 ms; the largest errors (about 1.1 to 1.4 m) are single catch-ups after 1.3 to 2.6 s frame stalls of the headless game at chapter load.
- Shared bodies: ids are a hash of scene name plus the transform path with sibling indices (`BodySync`); the host orders claims; every owner change raises an epoch (only the host raises it; a client's claim or release is applied locally at once and repeated every 300 ms until the host's newer grant arrives); only the owner at the current epoch may publish a state; grants are re-sent every second (16 per tick, round robin) and on join; an owner that leaves frees its bodies at a higher epoch. A first version let clients raise the epoch optimistically and diverged for good after packet loss (found by the 30%-loss convergence test); that is why the rule is now host-only. In the real game, two flat-build processes in chapter 0 (bots claim doors and set their spin; two doors each by one side, one door pushed from both sides at the same moment): all three doors ended with the same id, owner and epoch on both machines and orientations equal to 0.011 to 0.016 degrees (log precision).
- Invites (flat build, F7): `CoopLobby` uses Steam rich presence as the channel (host publishes `poolscoop_host`, a joiner publishes `poolscoop_join`, the host polls its friends every 2 s and adds those who ask to its allowed list; a claim only counts if the ID inside is the friend's own). Offline (fake Steam, strict accept mode, 10% loss) host-start, join-press and connect work end to end. Real Steam on this account: friends API returns 160 friends, rich-presence set/read/clear round trip works, the Host button created a Steam-transport session and published the presence, Stop cleared it. Screenshots of the panel in the flat main menu were taken (windowed run kept off-screen).
- Game log lines seen in the test runs (all from the game, none from the mod): `URPWaterHeightSampler: Compute shader missing 'CSMain' kernel` appears only with `-nographics` (no GPU); a run with a GPU (D3D11, `-batchmode` only) reached `MainMenu_VR` without it. `SteamManager.TryUpdateStat: SetStat(LEVELS_COMPLETED, 1) failed` is the game syncing its own progress to a Steam stat during a headless run; it failed, so nothing was written. `XR load definitely failed` and the OpenXR `xrCreateInstance failed` lines are the missing headset/runtime.
- Two-process bot run (`PoolsCoop\tools\run-pair.ps1`, host + client from two instances; the second instance is hardlinks of the first except `BepInEx`): client joined as player 1, host scene `MainMenu_VR` reached the client, about 980 and 1000 poses were accepted per side, every one equal to the sender's bot pose recomputed from its timestamp (0 bad), puppets were created (shader `HDRP/Unlit`), 0 malformed packets, saves unchanged. Two earlier runs reported FAIL on a puppet-lag metric; the cause was a 0.5 to 0.8 s main-thread stall in the sender around scene load (its pose then jumped about 0.45 m in one step), not a net bug. The metric now allows 0.4 s to settle after such a jump and reports `targetJumps` and `maxTargetJump`.
- `SingletonBehaviour<T>` exposes static `Instance` and `IsInstanced`. `PoolsXROrigin` exposes `xrOrigin` (its `Camera` is the head), `leftMotionControllerTracking` / `rightMotionControllerTracking`, `playerBody`, `rb`, `autoHands`. `SceneLoader.Load(Level, bool)` is a static entry that starts a chapter; `Level.scene` is the scene name string.

### Types that look relevant (names only; roles are guesses until read)

| Area | Types |
|---|---|
| VR rig | `Backrooms.Features.VR.PoolsXROrigin` (singleton, 186 methods), `PoolsVRManager`, `VRHandFollowTarget`, `VRHandModelSelector`, `VRHeightController`, `RigidbodyMoveProvider`, `CustomContinuousMoveProvider`, `SoftGrabInteractable`, `VRHeadCollision` |
| Flat player | `FPSController` (singleton), `rbFPSController`, `Backrooms.Features.Player.PlayerBody`, `SwimmingController` |
| Hand physics | `Autohand.AutoHandPlayer` (AutoHandAssembly, 201 types), `Autohand.Demo.XRHandControllerLink` |
| Chapters and scenes | `Backrooms.Features.Levels.LevelManager`, `Level` (ScriptableObject), `Backrooms.Scripts.SceneLoader`, `SceneAsyncLoader`, `ChapterSelectScreen` |
| Saves | `Backrooms.Features.AutoSave.AutoSave`, `LevelState`, `Saver`, `UnlockState` |
| Rewind | `Backrooms.Scripts.Utilities.CameraRewind`, `RewindSnapshot`, `RewindTrigger` |
| Dev console | `DevConsole` and commands `SpawnCommand`, `LoadCommand`, `SaveCommand`, `RewindCommand`, `UnlockCommand` |
| Platform | `Backrooms.Features.External.Steam.SteamManager`, `EpicManager`, `GOGManager` |
| World objects | `Door`, `HotelDoor`, `Chair`, `Backrooms.Features.Collectables.Collectable`, `Ladder`, `WaterSlide`, `BuoyancyObject` |

## Derived (reasoned from the observations, not tested)

- A mod can use BepInEx IL2CPP plus Harmony-style patches on `Assembly-CSharp` types.
- Remote players can be shown as head + two hands by reading the local rig's transforms (likely via `PoolsXROrigin`) and driving puppet objects on the other side.
- Steam networking has to use the old `ISteamNetworking` P2P API through the flat C exports of the game's own `steam_api64.dll` (see Observations): that DLL has no NetworkingMessages, and the game's own Steam code consumes the callbacks, so the plugin cannot see "session request" events.

## Guesses (unverified)

- Chapter transitions go through `SceneLoader`/`LevelManager`, so host-driven chapter sync may be a small patch.
- Physics props (doors, chairs, collectables) are the main extra sync surface; there seem to be no enemies, based on the store description.

## Unknown

- Whether the flat build's `Assembly-CSharp` matches the VR build's closely enough for one mod to serve both.
- How the game picks VR vs flat mode inside the VR build (`VRModeSwitcher`, `GameMode`).
- Whether Steam Cloud syncs a modified save if a test copy runs with the real app id.
- Anti-cheat: none seen in the file list; not confirmed.
- Whether the owner's friends play in VR.

## Log

1. Read `AGENTS.md`, `README.md`, `playbook/triage.md`; checked directory and issues.
2. Hashed exes, read the Steam manifest, listed plugins and managed assembly list.
3. With owner approval: created workspace, copied `POOLSVR`, installed BepInEx be.793 into the copy only, ran it once headless, dumped type names with Mono.Cecil.
4. With owner approval: installed a portable .NET SDK in the workspace, wrote and ran the read-only probe plugin (see Observations). Saves were byte-identical before and after every run.

5. With owner approval: second test instance (hardlinks), net layer, offline tests, bot pair run (see Observations).

## Not done / not verified

- Nobody has seen the puppets in a running co-op session (the runs are headless); only the panel was screenshotted. Shader `HDRP/Unlit` is found, so they should draw, but how they look is unknown.
- The real-VR path (reading head and hands from `PoolsXROrigin` when `XRSettings.isDeviceActive`) compiles and runs without a headset but has never produced a real pose. The owner cannot run the VR build yet, so VR was not tested beyond headless runs; the flat build is the tested one.
- Doors are only the `Door` + `HingeJoint` bodies and `BuoyancyObject` ducks. Pushing by a real hand or body (touch detection), grabbing a door handle by hand and ducks in real water have never been exercised by a person: the door test uses a bot that claims a door and sets its angular velocity.
- Chairs (sitting), ladders, water slides and the chapter progress/endings are not synced. A friend does not follow the host into a chapter; both start the same chapter from the menu.
- Water: a remote player's splash calls the game's ripple code and splash-particle spawner (verified to run and register ripples with culling off); the splash sound is not reproduced, and nobody has looked at the effect.
- A friend list lookup on this account found 160 friends, none in the game, so the Join button list has only been tested against a fake Steam. The host-side flow (Host button, presence set and cleared) ran for real.
- Steam transport is written and ran between two processes of ONE Steam account only. It has never carried traffic between two different accounts, so whether Steam delivers the very first packet from a friend, and whether a polled `AcceptP2PSessionWithUser` does anything, is unknown. The design avoids depending on either (both sides send first, host knocks at the allowed friends), and the fake-Steam tests pass under three Accept behaviours, but only a real friend can settle it.
- Invites exist for the flat build only (F7 panel). The VR panel is not built; the VR main menu has a `Side Buttons Panel` of world-space canvases (Settings / Exit VR / Quit Game) that a Co-op button could be cloned from.
- Players only share world coordinates; nothing aligns where each player starts (both start the same chapter at the same spawn).
- Loading a chapter in a test makes the game try to delete the autosave (`LevelManager.DeleteSaveFile`) and spin in a hot loop of save calls. Every chapter test therefore runs with `[Test] SaveGuard`, which stubs those writes and proves its own patches work before loading anything.

## Next

1. A real two-account test: the owner hosts, one friend joins with the plugin installed from `release\PoolsCoop-*.zip` (the friend's consent). Read the `transport:` and `status:` log lines on both sides.
2. A person pushes and grabs doors in the flat build (touch detection), and walks into water with a friend watching.
3. Host-driven chapter start through `SceneLoader.Load`, with save isolation.
4. VR: a panel cloned from the `Side Buttons Panel` buttons, and a real-pose check with a headset.

## 0.3.0 (2026-11-08): real hands, footsteps and splashes, VR menu

Measured in headless runs on the test copies (`-batchmode -nographics`, SaveGuard on, saves unchanged after every run):

- **Hands.** Both builds carry the full VR player rig in every chapter (inactive in the flat build): four Autohand hands
  (`VR-AutoHand-Male-Left`, `-Male-Right Variant`, `-Female-Left`, `-Female-Right Variant`; the right ones are the left mesh mirrored by a
  root scale of (-1,1,1)), each a `SkinnedMeshRenderer` `pools-vr-hands` / `pools-vr-hands-female` (3130 / 3481 vertices, 20 bones,
  root bone `hand.L`, shader `Shader Graphs/VRHand`, skin tone property `_SkinToneContrast` from `VRHandsMaterialManager`). There is
  **no body mesh** in VR; the game shows only hands. `HandTemplate` copies bone names, rest pose, mesh and materials once; the open and
  closed rotation of every finger joint are read from the game's own `Autohand.Finger.SetFingerBend(0/1, Open, Closed)` and the joints
  are put back in the same frame (`fingerPosesFromGame=True` for all four hands in both builds). Puppets get a plain copy (no physics,
  colliders or scripts) with fingers driven by 5 bend bytes per hand. The hand frame (finger direction, palm direction) is computed from
  the bones, not guessed: fingers along -X/+X of the root (left/right), palm along -Y.
- **Pose packet extension** (protocol unchanged, 12 trailing bytes after the three poses; older readers ignore them, a packet without them
  still reads): flags (VR, female hands, moving, swimming, hands-are-roots), skin tone, 5+5 finger bends. VR senders send the root pose of
  the game's physics hand (where it really is, stopped by walls), not the controller. Flat senders send hands hanging at the sides that
  swing while walking and reach forward while a mouse button is held.
- **Effects** (new message `Fx` = 13, unreliable, once, relayed by the host with the sender id enforced, 40/s limit). Harmony prefixes on
  `PlayerFootstepsAudioManager.PlayFootstepSound` (ground point, volume, direction, surface material, water depth),
  `RigidbodyRippleSpawner.Splash(enter)` (local body and hands only; the VR `XR Player Body` spawner has `isPlayer = false`, so the body is
  recognised by its `PlayerBody` component) and `SplashSpawner.PlaySplashDelayed`. The receiver calls `PlayFootstepSound` at the friend's
  ground point (the local wetness fields are saved and restored around the call), a ripple and spray in water, and for splashes the game's
  hand splash sounds (`enterWaterAudio`/`exitWaterAudio`, the body spawners have none) through `FMODUnity.RuntimeManager.PlayOneShot`.
  Pair run, flat build, chapter 0: each side sent 32 steps and 8 splashes through the game's own methods, the other side replayed 32/32 and
  8/8, 20 ripples, 20 sprays, 4 splash sounds, 0 failures. Same in the VR build.
- **Build difference found:** `RigidbodyRippleSpawner.vrHandWaterDetector` exists only in the VR build's game assembly. Using it threw
  `MissingMethodException` every frame in the flat build (it broke the method that used it). `build.ps1` now compiles the plugin a second
  time against the flat build's interop assemblies; it fails if anything used is missing there.
- **Objects:** in VR the touch points are the physics hands, and a body held by an Autohand hand (`Hand.holdingObj.body`) is claimed and
  never released while held.
- **Menus.** Flat: a `CO-OP (F7)` button at the top right whenever the cursor is free (seen in `MainMenu`), keys F7 and F8, the old Input
  Manager as a fallback, and a log line for every opening. VR: the panel and a `CO-OP` button are copies of the game's own world-space
  canvases (`Side Buttons Panel/.../Settings Button/Canvas`, layer 13, `TrackedDeviceGraphicRaycaster`) with everything but the canvas
  stripped. The button stands one step under `Quit Game` (measured y 1.22 -> 0.89). A test press of its `onClick` opened the panel 1.2 m in
  front of the head. In chapters the panel opens beside the game's `PauseCanvasVR/PauseScreen` while it is active.
- Offline tests: 231 checks, `ALL OK` (new: pose extension round trip, old packets, relay and buffer keep fingers; effects reach everyone
  once, sender id enforced, rate limited).
- Known test artefact: in the VR build the door bot's door index 4 is a door that does not move enough when spun, so neither side
  auto-claims it and the final orientations differ by 4.4 degrees (same in two runs). Not related to these changes; doors 0-3 pass.

Not verified: how the hands and the head/torso look (no rendering in the allowed test mode; `HDRP/Lit` is not reported in a
`-nographics` run, so the head and torso fall back to `HDRP/Unlit` there), the name label, finger bends from a real headset, the VR
button pressed by a real controller ray, footsteps and splashes heard by a person, the F7 problem on the owner's PC (needs its
`BepInEx\LogOutput.log`).

## 0.3.1 (2026-11-08): shared random sounds, water slosh, keep-alive fix

- Random sound sources in the game: `ScatteredOneShotAudioPlayer` (random FMOD event from a list at a random place on a random timer;
  chapter 0 has 2, both disabled until a script enables them), `OffScreenDetectorAudioPlayer` (chance roll when you look away, 0 in
  chapter 0), `PlayOneSoundTrigger` (3 in chapter 0), `OnStartRandomEvent` (chance roll in `Start`, 0 in chapter 0). Random scares that
  are `RandomEventTrigger` / trigger UnityEvents were already replayed by EventSync.
- **FMOD's `RuntimeManager.PlayOneShot` must not be Harmony-patched:** it returns `Il2CppSystem.Nullable<EventInstance>` and the
  native->managed trampoline throws `NullReferenceException` in `CreateGCHandle`; the chapter load then stalled. Calling it from the
  plugin is fine. So: the host runs its own copy of `ScatteredOneShotAudioPlayer.Update` (same fields: wait range, list, positions,
  offset, play limit) and sends the event GUID and place; clients in the host's chapter skip their copy. Offscreen and trigger players
  run the game's code; a postfix compares `spawns` / `_played` with the prefix value and sends `audioEventRef` when it played.
  `OnStartRandomEvent.Start` uses `SharedRandom.Unit(sessionSeed, objectId)` in co-op, so all machines take the same branch.
- New message `Sound` = 14 (from, seq, GUID as 4 x u32, position, source id, kind) with the scripted-event repeats and dedupe; the receiver
  plays it only if the source object exists in its own scene (and marks a `PlayOneSoundTrigger` as played). The host's seed travels as
  4 trailing bytes of `State`.
- Pair runs, chapter 0, flat and VR: the host's copy picked and played 2 random sounds, the client received and played them (plus one
  direct test sound each way), unknown sources 0, failures 0.
- Remote water: a friend's hand or swimming head moving fast along the surface plays the game's hand splash sounds (every 0.4-0.9 s).
- **Net fix found by the new test:** with some loss patterns the host timed out an idle client (pings once a second, no poses in the
  menu) while the client still believed it was connected and heard nothing for up to 5 s. The old event test failed the same way on
  that network seed (B got 0/40 relayed events). Now pings go every 500 ms and the host answers a packet from an unknown connection with
  `Bye` (at most once a second, never to a Bye), so the client re-joins at once. 246 offline checks pass, including that seed.

Not verified: a person hearing these sounds; `OffScreenDetectorAudioPlayer`, `PlayOneSoundTrigger` and `OnStartRandomEvent` firing in
a real run (none fired in the bot runs; their code paths only compiled and loaded).
