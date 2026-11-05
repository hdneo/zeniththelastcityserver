# ZenithSniffer – Guide

The sniffer records the complete network traffic of the Zenith client. **Goal: record as many real sessions as possible before the shutdown on December 18** – these captures are the most important basis for rebuilding the server faithfully later (login responses, PlayFab title data, entities, components, commands).

## Why a proxy and not just "listening"?

| Connection | Protection | Readable passively? |
|---|---|---|
| Skywave / Nexus / PlayFab (HTTP) | TLS (HTTPS) | no |
| ZenFabric (UDP, LiteNetLib) | RSA-2048 handshake + 2048-byte XOR key per session | only the handshake |

That's why there are two modes:

- **`proxy`** (recommended): the sniffer sits between the game and the original servers, forwards everything and stores it in plain text. For ZenFabric it swaps the RSA key during the handshake, reads the session key that way, and afterwards forwards all packets unchanged.
- **`passive`**: listen only (raw socket, administrator rights). Produces a `.pcap` for Wireshark and the handshake; the game content stays encrypted.

---

## 1. Download or build

**Ready-made package (easiest):** download `ZenithSniffer-<version>-win-x64.zip` from the [Releases page](https://github.com/hdneo/zenemu/releases), unzip it into any folder and continue with section 2. It contains `ZenithSniffer.exe` (no .NET installation needed), `sniffersettings.json` and this guide as `README.md`. Run all commands in that folder – `captures\` and `certs\` are created there.

**Build it yourself** – requirement: .NET 8 SDK.

```bash
dotnet build sniffer/ZenithSniffer -c Release
```

The EXE is then at `sniffer/ZenithSniffer/bin/Release/net8.0/ZenithSniffer.exe`. Run all commands below in the folder of that EXE (or with the full path).

## 2. One-time setup (proxy mode)

### 2a. Create the local certificate authority

```bash
ZenithSniffer.exe certs
```

This creates `certs/zenith-local-ca.cer` and shows the import command. **You** run the import yourself (for your Windows user only):

```bash
certutil -user -addstore Root "certs\zenith-local-ca.cer"
```

Only then does the client accept the certificates issued by the sniffer. Remove it again after capturing (`certs` shows this command too):

```bash
certutil -user -delstore Root <thumbprint>
```

> The CA's private key is in `certs/zenith-local-ca.pfx`. Never give this file to anyone – whoever has it can fake any website on your PC as long as the root certificate is imported.

### 2b. hosts file

Edit `C:\Windows\System32\drivers\etc\hosts` as administrator and append (if the sniffer runs on another PC, use its IP instead of 127.0.0.1):

```
127.0.0.1 nexus.zenithmmo.com
127.0.0.1 skywave.zenithmmo.com
127.0.0.1 4bb7b.playfabapi.com
```

Optionally also `127.0.0.1 ramenvr.sfo3.digitaloceanspaces.com` (the client's version list).

Then flush the DNS cache:

```bash
ipconfig /flushdns
```

The sniffer itself ignores the hosts file and asks 1.1.1.1 / 8.8.8.8 for the real addresses. It shows them at startup (`Upstream nexus.zenithmmo.com = …`) – if an error appears there, the original server is not reachable.

## 3. Capturing

1. Start the sniffer (needs ports 80/443 – run as administrator if necessary, and nothing else may use these ports):
   ```bash
   ZenithSniffer.exe proxy
   ```
2. Start Zenith normally, log in, choose a realm, play.
3. The console shows HTTP requests (`[HTTP] …`), the UDP bridge (`[UDP] Bridge 127.0.0.1:27777 -> …`) and all ZenFabric operations (`[1] S->C AddEntity …`).
4. Stop with `Ctrl+C`. Afterwards comment out the hosts entries (put `#` in front), otherwise the game keeps connecting through the sniffer.

**Tips for the most valuable captures:**
- Create a new character, character selection, tutorial, city, instance/dungeon, trading, inventory, crafting, groups, matchmaking – every area shows different commands and components.
- One session per topic (the sniffer creates a new folder per run).
- PlayFab title data (`Client/GetTitleData`) is loaded at login – it is especially important for our own server.
- Close other games while capturing (see troubleshooting: The Matrix Online).

### What is stored?

One folder per run, `captures/YYYYMMDD-HHMMSS-proxy/`:

| File | Contents |
|---|---|
| `zenfabric.log` | **readable**: every ZenFabric message decrypted – direction, time, IP:port, size, opcode, short info, hex block (32 bytes/line) and ASCII block (like the MxO packet logs) |
| `zenfabric-decrypted.pcap` | the same decrypted messages for **Wireshark** (as UDP packets between client and server address) |
| `http.log` | **readable**: every HTTP request/response with headers and body |
| `http.jsonl` | the same, machine-readable; the original realm list as `realm-list-original` |
| `zenfabric.jsonl` | every ZenFabric operation decoded (opcode, entity, component name, data as hex) |
| `zenfabric.zfcap` | all decrypted ZenFabric payloads, binary – the basis for later analysis |
| `sessions.jsonl` | connections, join/identity tokens, session keys |

> **Warning:** capture folders contain login tokens, e-mail, account IDs and character names – including those of **other players**. Never share the folder itself, only an export (see "Sharing a capture").

### Viewing a capture

```bash
ZenithSniffer.exe decode captures\<folder>\zenfabric.zfcap
ZenithSniffer.exe decode captures\<folder>\zenfabric.zfcap --json
```

### Sharing a capture (community)

```bash
ZenithSniffer.exe export captures\20261104-210818-proxy
```

This creates `…-proxy-export\` and `…-proxy-export.zip` next to it. **Only upload the ZIP file.**

What the export does:

| | |
|---|---|
| **left out** | `sessions.jsonl` (tokens, session keys), passive raw data (`zenith-udp.pcap`, `udp-raw.log` – contain your own IP), encrypted passive records, all other files in the folder |
| **replaced** | login tokens (JWT), session tickets, `Authorization`/`X-Authorization`/cookie headers, e-mail, user name, Steam/Oculus/PlayFab IDs (also of other players), all values from account components (`PlayerAuthComponent`, `CharacterCredentialsComponent`: character name, Vivox/store account, referral codes …), worker IDs; client IP → `10.0.0.2` |
| **regenerated** | `zenfabric.log`, `.jsonl`, `.zfcap`, `-decrypted.pcap`, `http.log`, `http.jsonl` – from the sanitized data |
| **self-check** | At the end the export is searched for every original value. If anything is left, you get a **WARNING** and "DO NOT SHARE" (exit code 2) |

Values are replaced by **pseudonyms of the same length and shape** (letters stay letters, hex stays hex). The game data therefore stays exactly readable, and the same player has the same pseudonym throughout the export – you can still follow "who does what". The pseudonyms cannot be reversed.

The export also writes `_export-mapping-DO-NOT-SHARE.txt` into the source folder (original → pseudonym, for your own checking only). **Never** upload this file.

**Cannot be removed automatically:** freely typed text, e.g. chat messages, guild or house names. So don't write anything personal in chat while capturing.

Also never upload the `certs\` folder (it contains the private key of your certificate authority).

## 4. Passive mode

As administrator:

```bash
ZenithSniffer.exe passive
ZenithSniffer.exe passive --ip 192.168.1.20 --port 7777
```

- Detection: at connection setup (ConnectRequest with protocol ID 13) **or** – even while the game is already running – by the Zenith-specific 69-byte ACK packets. `--port` sets the server port explicitly.
- Result: `zenith-udp.pcap` (Wireshark), `udp-raw.log` (every UDP packet readable with LiteNetLib info: sequence, channel, fragment) and `zenfabric.log`/`zenfabric.jsonl` with the handshake. Game content after that is marked as "encrypted".
- Pure ACK/ping packets are only in the pcap; with `--acks` also in `udp-raw.log`.
- The Windows firewall can restrict raw sockets; use proxy mode in that case.

## 5. Settings (`sniffersettings.json`)

| Key | Default | Meaning |
|---|---|---|
| `BindAddress` | `0.0.0.0` | local address of the listeners |
| `HttpPort` / `HttpsPort` | `80` / `443` | `0` = off |
| `AdvertiseHost` | `127.0.0.1` | address given to the game as UDP server (the sniffer PC's IP if it runs elsewhere) |
| `UdpPortBase` | `27777` | first local UDP port of the bridges |
| `UpstreamDns` | `1.1.1.1`, `8.8.8.8` | DNS for the real servers |
| `HostOverrides` | – | fixed IP per host name, e.g. `{"nexus.zenithmmo.com": "104.26.13.64"}` |
| `UpstreamOverrides` | – | redirect a target completely: `["host:port=ip:port"]` (e.g. for testing against our own server) |
| `RewriteRealmList` | `true` | rewrite the realm list to the UDP bridge (needed for ZenFabric capture) |
| `OutputDirectory` | `captures` | output folder |
| `CertificateDirectory` | `certs` | folder of the local CA |
| `VerboseConsole` | `true` | print operations to the console |
| `TextLogMaxBytes` | `65536` | max. bytes per packet in the hex/ASCII text (0 = everything; the rest is always in pcap/zfcap) |
| `QuietComponents` | `[54, 11000]` | component updates hidden on the console (position, transform) |

Single values can also be set on the command line: `ZenithSniffer.exe proxy HttpsPort=8443 AdvertiseHost=192.168.1.20`.

## 6. Troubleshooting

| Problem | Cause / solution |
|---|---|
| Port 80/443 in use | IIS, Skype, another web server → stop it or run as admin |
| Game reports a connection error at login | root certificate not imported or hosts entry missing; `ipconfig /flushdns` |
| `[HTTP] ERROR … DNS resolution` | internet/DNS blocked → set `HostOverrides` |
| HTTP works, but no `[UDP]` lines | realm list not detected → look for `api/realm/list` in `http.jsonl` and share the structure with the project |
| Client sends, server stops answering after a short time; game reports it can't connect to defender_eu | Fixed since this version: Zenith uses a modified LiteNetLib (window size 512). Older sniffer builds stalled at the first large message |
| Thousands of `ad.thematrixonline.net` requests in `http.log` | The Matrix Online was running at the same time and its ad traffic hits port 80. Close MxO while capturing (or remove that hosts entry) |
| `[UDP n] … not decodable` | please keep the `zenfabric.zfcap` – it points to a protocol deviation |

## 7. Test without the game

Server + sniffer + test client locally (see `docs/06_Server.md`, section "Test without the game"):

```json
{ "HttpPort": 28080, "HttpsPort": 0, "UpstreamOverrides": [ "localhost:28080=127.0.0.1:18080" ] }
```

```bash
dotnet run --project tools/ZenithTestClient -- --nexus http://localhost:28080
```
