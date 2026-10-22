# Megacity Metro on Edgegap dedicated servers and matchmaking

A fork of Unity's [Megacity Metro](https://github.com/Unity-Technologies/megacity-metro) (Netcode for Entities, up to 150 players) set up as an Edgegap sample. **Find Match** goes through the Edgegap matchmaker, which starts a dedicated server on Edgegap near the players. Players who search later join running servers through backfill, up to 150 per server. The server stops its own deployment when nobody is playing. Desktop clients can also connect to a server directly.

An Android APK builds with the same Find Match flow; it has not been played on a phone yet (see [Not done yet](#not-done-yet)).

The game's netcode is unchanged. The Edgegap pieces are a few new files, plus small hooks in two upstream files:

| File | What it does |
|---|---|
| `Assets/Scripts/Gameplay/Mix/Multiplayer/Edgegap/EdgegapEnvironment.cs` | Reads what Edgegap injects into a deployment, and the client's direct-connect target (`-edgegapConnect` / `EDGEGAP_CONNECT`) |
| `Assets/Scripts/Gameplay/Mix/Multiplayer/Edgegap/EdgegapServerLifecycleSystem.cs` | Server world, Edgegap only: stops the deployment when nobody joins or everyone has left |
| `Assets/Edgegap/Editor/EdgegapServerBuild.cs` | **Tools → Edgegap → Build Linux Dedicated Server**, also callable from the command line |
| `Dockerfile`, `.dockerignore` | Server image from `Builds/EdgegapServer/` |
| `Assets/Scripts/Gameplay/Mix/Multiplayer/SDK/Server/ServerManager.cs` | On Edgegap, skips Unity Gaming Services (UGS) sessions |
| `Assets/Scripts/Gameplay/Mix/UI/MonoBehaviours/Matchmaker/MatchMakingConnector.cs` | Find Match uses the Edgegap matchmaker; with a direct-connect target, connects straight to the server. Either way, skips UGS sign-in |
| `Assets/Scripts/Gameplay/Mix/Multiplayer/Edgegap/EdgegapMatchmakerSettings.cs`, `Assets/Resources/EdgegapMatchmakerSettings.asset` | Matchmaker URL, auth token, profile and backfill settings, shared by client and server |
| `Assets/Scripts/Gameplay/Mix/Multiplayer/Edgegap/EdgegapMatchmakerClient.cs` | Client: queues a solo ticket and returns the server's IP and port |
| `Assets/Scripts/Gameplay/Mix/Multiplayer/Edgegap/EdgegapBackfillSystem.cs` | Server world, matchmaker deployments only: keeps backfills open while seats are free |
| `Assets/Scripts/Gameplay/Mix/Multiplayer/Edgegap/EdgegapTicketRpc.cs` | Client sends its matchmaker ticket ID after connecting, so the server can track seats |
| `matchmaker-config.json` | Matchmaker configuration to upload in the dashboard |

The matchmaker client and server backfill use the [Edgegap Unity SDK](https://github.com/edgegap/edgegap-unity-sdk) (`com.edgegap.unity-sdk` 3.5.5, in `Packages/manifest.json`).

With an empty matchmaker URL in `EdgegapMatchmakerSettings`, outside an Edgegap deployment (no `ARBITRIUM_REQUEST_ID`), and without a direct-connect target, the game behaves exactly like upstream.

## Prerequisites

- Unity `6000.1.0f1` with the **Linux Build Support (IL2CPP)**, **Linux Build Support (Mono)** and **Linux Dedicated Server Build Support** modules, plus the module for your client platform (Windows Build Support (IL2CPP) for the tested client).
- Docker, and an [Edgegap account](https://app.edgegap.com).
- Git LFS: the project is about 415 MB of LFS content.

## 1. Build the server

Close the project in the Editor first, then from the project root:

```bash
Unity -batchmode -quit -buildTarget Linux64 -standaloneBuildSubtarget Server -projectPath . -executeMethod EdgegapServerBuild.Build -logFile build.log
```

Keep `-buildTarget Linux64 -standaloneBuildSubtarget Server`. Without them the Entities content build fails with `Unable to build with the current configuration`.

The output goes to `Builds/EdgegapServer/` (`ServerBuild`, `ServerBuild_Data`, `UnityPlayer.so`). The folder is about 3.4 GB, mostly Burst debug information and the IL2CPP backup folder. `.dockerignore` leaves out `*_BurstDebugInformation_DoNotShip` and `*_BackUpThisFolder_ButDontShipItWithYourGame`, so the image is about 537 MB.

## 2. Build the image and push it

```bash
docker login registry.edgegap.com
docker build --platform linux/amd64 -t registry.edgegap.com/<project>/megacity-metro:<tag> .
docker push registry.edgegap.com/<project>/megacity-metro:<tag>
```

Your registry project, username and token are on the dashboard's **Container Registry** page. The server listens on `0.0.0.0:7979` over UDP. Extra server arguments can be passed in the `UNITY_COMMANDLINE_ARGS` environment variable on the app version.

## 3. Create the app version

In the [dashboard](https://app.edgegap.com), create an application and a version for the image:

- Port: name `gameport`, port `7979`, protocol **UDP**.
- 1 vCPU / 2 GB (tested). An idle server used about 18% CPU and 323 MB.
- Container registry username and token, or deployments fail to pull the image.

## 4. Deploy and connect a desktop client

Deploy the version from the dashboard. The server log shows `Running on Edgegap ... skipping UGS sessions`.

Start a desktop client with the deployment's host and **external** port (shown on the deployment page, it is not 7979):

```bash
<client executable> -edgegapConnect <host>:<external port>
```

or set `EDGEGAP_CONNECT=<host>:<external port>` before launching it. The host can be the deployment's IP or its FQDN (`<id>.pr.edgegap.net`); the client resolves it to IPv4. With a direct-connect target the client skips UGS sign-in, so it needs no UGS project. The Find Match button and the batch-mode auto-matchmake flow both connect to that target.

The server logs `[Edgegap] Connected players: 1` when the client joins.

## 5. Create the matchmaker

1. Set `application.version` in `matchmaker-config.json` to your app version. Profile `metro`: one team of 2 to 150 players. Two players searching together get one server about 10 s after they queue, and a lone player gets one after 30 s. That suits testing; raise the minimum for real matches.
2. In the dashboard, open **Matchmaker → Create Matchmaker**, upload `matchmaker-config.json`, then click **Create and Start**.
3. When it shows ONLINE, copy the **API URL** and **Auth Token** into `Assets/Resources/EdgegapMatchmakerSettings.asset`. The auth token belongs to the matchmaker and grants no access to your Edgegap account, so it is safe to ship in clients.
4. Rebuild the server and the clients. Both read this asset: the client to find matches, the server to create backfills.

`-edgegapMatchmakerUrl`, `-edgegapMatchmakerToken` and `-edgegapMatchmakerProfile` (or `EDGEGAP_MM_URL`, `EDGEGAP_MM_TOKEN`, `EDGEGAP_MM_PROFILE`) override the asset at launch. On the free tier, the matchmaker shuts down 3 hours after each start; restart it from the dashboard. Its URL and token stay the same.

Click **Find Match** in a client. Batch-mode clients (`-batchmode -nographics`) matchmake on their own. The status goes `Searching...`, `Match found, starting server...`, then the client connects.

## 6. Build the clients

Close the project in the Editor first, then run one of these from the project root:

| Client | Command | Output |
|---|---|---|
| Windows, quick test build (Mono, minutes) | `<Unity> -batchmode -quit -buildTarget Win64 -standaloneBuildSubtarget Player -projectPath . -executeMethod EdgegapServerBuild.BuildClient -edgegapClientTarget win64 -edgegapClientMono -logFile client.log` | `Builds/Client/win64-mono/Game.exe` |
| Windows, project settings (IL2CPP Master, ~40 min, ~12 GB RAM at link) | same, without `-edgegapClientMono` | `Builds/Client/win64/Game.exe` |
| Android (IL2CPP, ARM64) | `<Unity> -batchmode -quit -buildTarget Android -projectPath . -executeMethod EdgegapServerBuild.BuildClient -edgegapClientTarget android -logFile android.log` | `Builds/Client/android/MegacityMetro.apk` |
| Android for the emulator (ARM64 + x86_64) | same, plus `-edgegapAndroidX86_64` | `Builds/Client/android/MegacityMetro-arm64-x86_64.apk` |

Android needs the **Android Build Support** module with its SDK, NDK and OpenJDK. The APK is signed with Unity's debug key, so it installs by sideloading (`adb install`, or copying it to the phone and allowing installs from unknown sources); use your own keystore and an App Bundle for a store build. Mobile release builds hide the Connect option, so phones always use Find Match.

The Android build uses Vulkan, with OpenGL ES 3.1 as a fallback (Player settings, **Require ES3.1**). Entities Graphics needs compute shaders, so devices with only OpenGL ES 3.0 can't run it.

### Run it in the Android Emulator

The emulator can stand in for a phone on a PC with a dedicated GPU. Use an x86_64 system image and the `-edgegapAndroidX86_64` APK, which runs natively there instead of through ARM translation.

1. Install the emulator and an image, for example with the SDK manager from Unity's Android module (it asks you to accept the Android SDK licences): `sdkmanager --sdk_root=<sdk> emulator platform-tools "system-images;android-34;google_apis;x86_64"`, then create a virtual device (Android Studio's Device Manager, or `avdmanager`). 4 GB of RAM is enough.
2. Start it on the host GPU **with its Vulkan support turned off**: `emulator -avd <name> -gpu host -feature -Vulkan`. With software graphics, Unity can't start its graphics API; with the emulator's Vulkan, the emulator crashes ("bad color buffer handle") as soon as the game renders. With Vulkan off, the game uses the OpenGL ES fallback.
3. `adb install -r MegacityMetro-arm64-x86_64.apk`, open **Megacity Metro**, and decline the microphone prompt (Vivox voice chat, which the Edgegap path doesn't use). Then **Multiplayer → Find Match**.

## Test it yourself

1. Make sure the matchmaker shows ONLINE in the dashboard (free tier: restart it if it stopped).
2. Start a Windows client and click **Multiplayer**, then **Find Match**. A lone player gets a server after about 30 s; the first deployment needs about a minute more to load before the client connects.
3. On an Android phone (or the [emulator](#run-it-in-the-android-emulator)), install the APK, open it and click **Multiplayer → Find Match**. While the first match is running, the phone joins the same server through backfill.
4. In the dashboard, the deployment is tagged with the ticket IDs. Its log shows `[Edgegap] Connected players: N` and `Matched player connected` for each player.
5. Quit every client. The deployment stops itself about 60 s after the last player leaves.

## Backfill (join in progress)

A backfill is a ticket owned by a running server. When a newly queued player matches it, the matchmaker sends that player to this server instead of starting a new one. Every ticket carries a `backfill_group_size` attribute: clients send `["new", "1"]` ("a new match, or one seat on a running server"), and the server sends `["1"]`.

On matchmaker deployments, `EdgegapBackfillSystem`:

- reads the match from the `MM_*` variables and gives each matched player 60 s to connect,
- keeps up to 3 backfills open while seats are free (150 minus connected players minus players on the way). `EDGEGAP_MAX_PLAYERS` and `EDGEGAP_MAX_OPEN_BACKFILLS` on the app version change these limits,
- turns a matched backfill into a seat for the new player, then opens another backfill,
- matches connections to tickets with `EdgegapTicketRpc`, and frees a seat when its player leaves or never arrives,
- closes all backfills before the server stops itself.

Players who are matched but still connecting keep the server from self-stopping.

One known gap: if a deployment is stopped from outside (dashboard or API stop, or the max duration), the Unity player exits on SIGTERM without closing its backfills. Until they expire (`ticket_expiration_period`, 5 min), they can send a player to the stopped server. That player's connection times out, and the next Find Match works.

## Self-stop

Inside an Edgegap deployment, `EdgegapServerLifecycleSystem` stops the deployment through `ARBITRIUM_DELETE_URL`:

- 300 s after start if no player ever connected (`EDGEGAP_FIRST_PLAYER_TIMEOUT_S`),
- 60 s after the last player left (`EDGEGAP_EMPTY_TIMEOUT_S`).

Set either variable on the app version to change the timeout. If the stop request fails, or the URL or token is missing, the server quits instead. Server log lines start with `[Edgegap]`.

## Tested

2026-10-20, Unity 6000.1.0f1, app version with 1 vCPU / 2 GB and 7979/UDP:

- A Windows IL2CPP client started with `-batchmode -nographics -edgegapConnect <ip>:<port>` connected to the Edgegap deployment, and the server logged the player joining.
- Self-stop with no player: the deployment stopped at 1502 s with the timeout set to 1500 s.
- Self-stop when empty: the deployment stopped about 60 s after the client quit.

The Windows client is an IL2CPP Master build: it took about 40 minutes and the linker used about 12 GB of RAM.

2026-10-21, matchmaker (free tier) with the `metro` profile, app version with 1 vCPU / 2 GB:

- Two Windows clients (Mono test builds, `-batchmode -nographics`) queued together, got `MATCH_FOUND` then `HOST_ASSIGNED` for the same deployment, and both connected. The server matched both tickets to their connections.
- A third client queued while that match was running. It went from `SEARCHING` straight to `HOST_ASSIGNED` on the same deployment through a backfill, and joined (3 players).
- After all three quit, the deployment stopped itself.
- Locally (server in Docker, mocked Edgegap variables, real matchmaker): when no matched player connected within the 60 s grace, the seats were released and the backfills closed.

For quick test clients, `-executeMethod EdgegapServerBuild.BuildClient -edgegapClientTarget win64 -edgegapClientMono -buildTarget Win64 -standaloneBuildSubtarget Player` builds with Mono into `Builds/Client/win64-mono/`. Pass `-standaloneBuildSubtarget Player` after a server build, or the build fails with "script class layout is incompatible".

2026-10-22, matchmaker restarted, app version v2: two Windows Mono clients (`-batchmode -nographics`) matched together onto one deployment, connected, and the server logged both tickets with backfills open. The Android APK built (IL2CPP, ARM64, API 23 to 35) but was not run on a device.

2026-10-22, Android Emulator (Android 14 x86_64 image, `-gpu host -feature -Vulkan`, NVIDIA RTX 3050 laptop GPU): the ARM64 + x86_64 APK with the OpenGL ES fallback reached the main menu, and its matchmaker client reported the matchmaker healthy. Find Match was not run from the emulator.

## Not done yet

- **A phone playtest** of the Android APK, and a windowed playtest on desktop. **iOS** needs a Mac to build.
- **Ticket validation.** The server logs a connection whose ticket it was not given, but still lets it in.
- Vivox and UGS sign-in are untouched. The Edgegap matchmaker and direct-connect paths don't use them, and the Connect mode's session browser still lists UGS sessions.
