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

If server and client run on the same PC: use `127.0.0.1` everywhere.

### Notes

- The hosts entries only affect these three domains. Other services (Oculus, Steam, Vivox voice chat, Sentry) keep going to the originals or disappear after the shutdown.
- The platform login (`loginrift`/`loginsteam`) sends the server an Oculus or Steam proof. ZenithServer does not verify it and creates an account per platform user ID automatically (`Auth.AutoRegister`).
- Confirmed by the sniffer: the game trusts a CA imported into the Windows **user** certificate store.
- After December 18 the original servers are gone – make captures (see `05_Sniffer.md`) **now**.

## 3. Without a hosts file on every PC

- **Domain transfer:** if the community obtains `zenithmmo.com` (handed over by Ramen VR or after it expires), you point `nexus`/`skywave` at your server in normal DNS and use a free Let's Encrypt certificate – unmodified clients then work without any setup. PlayFab (`4bb7b.playfabapi.com`) belongs to Microsoft and cannot be taken over.
- **Central DNS:** a DNS server for all players (router, Pi-hole, own DNS) answers the game domains with your server's IP. Each player still needs to import the root certificate once.
- **Community launcher:** a launcher that sets up the redirect and certificate on first start. Keep the CA's private key offline and never distribute the `.pfx`.
