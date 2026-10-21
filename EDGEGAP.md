# Megacity Metro on Edgegap dedicated servers

A fork of Unity's [Megacity Metro](https://github.com/Unity-Technologies/megacity-metro) (Netcode for Entities, up to 150 players) set up as an Edgegap sample. The dedicated server runs on Edgegap, desktop clients connect to it directly, and the server stops its own deployment when nobody is playing.

This is step 1. The Edgegap matchmaker is not done yet, and neither are mobile clients (see [Not done yet](#not-done-yet)).

The game's netcode is unchanged. The Edgegap pieces are a few new files, plus small hooks in two upstream files:

| File | What it does |
|---|---|
| `Assets/Scripts/Gameplay/Mix/Multiplayer/Edgegap/EdgegapEnvironment.cs` | Reads what Edgegap injects into a deployment, and the client's direct-connect target (`-edgegapConnect` / `EDGEGAP_CONNECT`) |
| `Assets/Scripts/Gameplay/Mix/Multiplayer/Edgegap/EdgegapServerLifecycleSystem.cs` | Server world, Edgegap only: stops the deployment when nobody joins or everyone has left |
| `Assets/Edgegap/Editor/EdgegapServerBuild.cs` | **Tools → Edgegap → Build Linux Dedicated Server**, also callable from the command line |
| `Dockerfile`, `.dockerignore` | Server image from `Builds/EdgegapServer/` |
| `Assets/Scripts/Gameplay/Mix/Multiplayer/SDK/Server/ServerManager.cs` | On Edgegap, skips Unity Gaming Services (UGS) sessions |
| `Assets/Scripts/Gameplay/Mix/UI/MonoBehaviours/Matchmaker/MatchMakingConnector.cs` | With a direct-connect target, skips UGS sign-in and connects straight to the server |

Outside an Edgegap deployment (no `ARBITRIUM_REQUEST_ID`) and without a direct-connect target, the game behaves exactly like upstream.

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

## Not done yet

- **Edgegap matchmaker.** Mobile release builds hide the Connect option, so phones need a matchmaker to find a server. A 150-player game also needs backfill so players can join a match in progress.
- **Android and iOS clients**, and a windowed playtest on desktop.
- Vivox and UGS sign-in are untouched. The direct-connect path doesn't use them.
