# Server addresses: where are they, and how do you play on your own server?

## 1. How the client finds its servers

The client has **no** hard-coded game server IP. The chain is:

```
ReleaseConfiguration (Unity asset)            ──►  base URLs (HTTPS)
   │
   ├─ AuthSkywaveURL  https://skywave.zenithmmo.com/api/game   login (loginrift/loginsteam, checkrift/checksteam, …)
   ├─ AuthConfigURL   https://nexus.zenithmmo.com              realm list + join
   ├─ MisoURL         https://nexus.zenithmmo.com              matchmaking
   └─ PlayFabTitleId  4BB7B   ──► https://4bb7b.playfabapi.com  PlayFab (data, inventory, …)

GET  {AuthConfigURL}/api/realm/list   ──►  { "<id>": { …, "workerHost": "…", "workerPort": n } }
POST {AuthConfigURL}/api/realm/join   ──►  joinToken + identityToken
UDP  workerHost:workerPort            ──►  ZenFabric (game world)
```

**So the UDP address of the game server comes from the HTTP server.** Whoever serves the HTTP endpoints also decides where the client connects via UDP.

### Locations in the client (for information only)

| What | File | Details |
|---|---|---|
| `ReleaseConfiguration` (Development / Staging / Production / Legacy) | `UnityClient@Windows_Data/resources.assets` | object around file offset ≈ 260,087,268. Production: `https://nexus.zenithmmo.com`, tag `prod-build40`, `https://skywave.zenithmmo.com/api/game`, PlayFab `4BB7B` |
| `NoodleAuthConfig` | `resources.assets` | from ≈ 260,086,516: ProjectID `beta_mexico_eight_889`, `api/realm/join`, `api/realm/list`, status URL `https://nexus.zenithmmo.com/api/status` |
| PlayFab domain | `il2cpp_data/Metadata/global-metadata.dat` | string literal `playfabapi.com` (the PlayFab SDK builds `https://{TitleId}.playfabapi.com` from it) |
| Version list | `level1` | `https://ramenvr.sfo3.digitaloceanspaces.com/builds/client_versions.json` |
| Skywave API key | code (`AuthClientSkywave.SKYWAVE_AUTH`) | `Bearer be4ba5c7-…` – fixed header, not a user secret |

Which of the four environments is active is decided by `ReleaseConfiguration.GetCurrent()` (PTR check, normally Production).

This project intentionally does not include instructions for modifying the game files. The approach below works without changing the client.

## 2. Using your own server – without changing the client

Principle: the original domains point to your own server via the **hosts file**, and the server identifies itself with certificates from **its own, locally trusted certificate authority**. ZenithServer creates this CA automatically and issues a matching certificate for every requested host name (SNI).

### On the server PC

1. Adjust `server/ZenithServer/appsettings.json`:
   ```json
   "PublicHost": "192.168.1.50",
   "Http": { "HttpPort": 80, "HttpsPort": 443 },
   "ZenFabric": { "Port": 7777 }
   ```
   `PublicHost` is the address players use to reach the server via **UDP** (LAN IP or public IP/DNS name). It is written into the realm list.
2. Firewall/router: allow or forward TCP 80 + 443 and **UDP 7777**.
3. Start the server:
   ```bash
   dotnet run --project server/ZenithServer -c Release
   ```
4. Give `server/ZenithServer/data/certs/zenith-local-ca.cer` to the players (only the `.cer`, **never** the `.pfx`).

### On every player PC

1. Import the root certificate for your own user (run it yourself):
   ```bash
   certutil -user -addstore Root zenith-local-ca.cer
   ```
2. Add to the hosts file (`C:\Windows\System32\drivers\etc\hosts`, as administrator) – the server's IP:
   ```
   192.168.1.50 nexus.zenithmmo.com
   192.168.1.50 skywave.zenithmmo.com
   192.168.1.50 4bb7b.playfabapi.com
   ```
3. `ipconfig /flushdns`, then start Zenith.

To undo: remove the lines from the hosts file and run `certutil -user -delstore Root <thumbprint>`.

### Same-PC Windows setup

From the server repository root, start a local-only instance in PowerShell:

```powershell
dotnet run --project server/ZenithServer -c Release -- --Zenith:Http:BindAddress=127.0.0.1 --Zenith:ZenFabric:BindAddress=127.0.0.1 --Zenith:Auth:AllowUnknownIdentityTokens=false --Zenith:Http:LogBodies=false --Zenith:ZenFabric:CapturePackets=false
```

The server prints the generated certificate path on startup. Install the certificate for the Windows user who launches the game.

With Node.js installed, run the client through the process-local proxy from the server repository root:

```powershell
node tools/launch-local-client.mjs "..\Zenith MMO\UnityClient@Windows.exe"
```

The launcher sets `HTTPS_PROXY` for this instance of Unity. It sends Zenith, PlayFab and the build metadata requests to the local server while keeping their original TLS hostnames. Set `ZENITH_LOCAL_HTTPS_PORT` to match the server if you changed its HTTPS port. Unity options can follow the executable path.

For the hosts file setup, use `127.0.0.1` for the three entries above and add the build metadata host:

```text
127.0.0.1 ramenvr.sfo3.digitaloceanspaces.com
```

Use a signed-in Steam session with SteamVR running. In the server console, `GET /api/status` and `GET /api/build/56557` confirm the Unity client reached local HTTPS. After platform login, look for `/api/realm/join` and `ZenFabric: worker authenticated` to confirm the UDP connection. See [server status](06_Server.md) for the implemented character-selection steps.

### Connecting from a second PC on the LAN

Set the server's `Zenith:PublicHost` to its LAN IP. Bind HTTPS and ZenFabric UDP to the LAN IP or `0.0.0.0`, and allow incoming TCP 443 and UDP 7777 on the LAN. Set `Zenith:Auth:AllowUnknownIdentityTokens` to `false`.

On each player PC, import the server's `.cer` for that Windows user. Set the launcher target to the server's LAN IP in the terminal you use to launch the game.

```powershell
$env:ZENITH_LOCAL_HOST = '192.168.1.50'
node tools/launch-local-client.mjs 'D:\Games\Zenith\UnityClient@Windows.exe'
```

Use a separate game account for each player. Choose the same realm and enter the same area to see each other.

### Testing startup without a headset

SteamVR includes a virtual headset driver called `null`. Exit any running SteamVR session first. In the PowerShell session you will use to launch Zenith, set up a separate SteamVR profile:

```powershell
$env:VR_OVERRIDE = 'C:\Program Files (x86)\Steam\steamapps\common\SteamVR'
$env:VR_CONFIG_PATH = Join-Path $env:LOCALAPPDATA 'ZenithDev\steamvr-null\config'
$env:VR_LOG_PATH = Join-Path $env:LOCALAPPDATA 'ZenithDev\steamvr-null\logs'
New-Item -ItemType Directory -Force $env:VR_CONFIG_PATH, $env:VR_LOG_PATH | Out-Null
@'
{
  "steamvr": { "forcedDriver": "null", "requireHmd": false, "enableHomeApp": false },
  "driver_null": { "enable": true }
}
'@ | Set-Content (Join-Path $env:VR_CONFIG_PATH 'steamvr.vrsettings') -Encoding utf8
& (Join-Path $env:VR_OVERRIDE 'bin\win64\vrstartup.exe')
```

Run the Zenith launcher in that same PowerShell session. To test through character selection without controllers, use the client's simulation sign-in. Use disposable local credentials because Unity logs its command-line arguments.

```powershell
$instructions = Join-Path $env:TEMP 'zenith-empty-instructions.json'
'{"doScreenshots":false,"instructions":[]}' | Set-Content $instructions -Encoding utf8
node tools/launch-local-client.mjs "..\Zenith MMO\UnityClient@Windows.exe" `
  -zenithSimPlayer true -zenithInstructionsFile $instructions `
  -zenithUsername LocalVrTest -zenithPassword LocalVrTestOnly `
  -zenithDeploymentName zenith-local-1
```

Steam selects Infinite Realms when The Last City DLC is absent. The default local realm includes the `prod-build40_rei` tag so the Infinite Realms client can discover it. Include that tag when configuring your own realm list. The captured character commands in this project come from The Last City.

To test world entry, append `-zenithCharacterName` followed by the name of a character saved on the disposable account. The simulation exits after it reaches the world and finishes its instruction list.

### Notes

- Steam, Oculus, Vivox voice chat and Sentry still use their own service domains.
- The platform login (`loginrift`/`loginsteam`) sends the server an Oculus or Steam proof. ZenithServer does not verify it and creates an account per platform user ID automatically (`Auth.AutoRegister`).
- Confirmed by the sniffer: the game trusts a CA imported into the Windows **user** certificate store.
- After December 18 the original servers are gone – make captures (see `05_Sniffer.md`) **now**.

## 3. Without a hosts file on every PC

- **Domain transfer:** if the community obtains `zenithmmo.com` (handed over by Ramen VR or after it expires), you point `nexus`/`skywave` at your server in normal DNS and use a free Let's Encrypt certificate – unmodified clients then work without any setup. PlayFab (`4bb7b.playfabapi.com`) belongs to Microsoft and cannot be taken over.
- **Central DNS:** a DNS server for all players (router, Pi-hole, own DNS) answers the game domains with your server's IP. Each player still needs to import the root certificate once.
- **Community launcher:** a launcher that sets up the redirect and certificate on first start. Keep the CA's private key offline and never distribute the `.pfx`.
