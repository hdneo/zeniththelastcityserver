# HTTP APIs of the client

Field names exactly as in the client classes (case-sensitive, Unity `JsonUtility`). Responses whose format is only estimated are marked – verify them with the sniffer.

## Login flow

1. `AuthClientSkywave` checks the account and logs in with the platform proof (Oculus or Steam) → `sessionTicket`, `authToken`, `playFabId`.
2. `NoodleAuthClient` fetches the realms → player choice or automatic selection.
3. `POST api/realm/join` → `joinToken`, `identityToken` (stored in `AuthCredentials`).
4. `ZenithClientWorkerConnector` fetches the realm list again (headers `Authorization`/`Authentication: Bearer <authToken>`, `X-Zenith-Client-Version`) and connects via UDP to `workerHost:workerPort`.

## Skywave – `https://skywave.zenithmmo.com/api/game`

Header: `Authorization: Bearer be4ba5c7-4314-48c6-8b97-9991cae08c51` (hard-coded in the client).
The Oculus version uses `…rift`, the Steam version `…steam` (`checksteam`, `loginsteam`, …) – same response formats according to captures. Real `loginsteam` response: `{"errorCode":0,"message":"Logged in using Native ID","success":true,"sessionTicket":"…-4BB7B-…", …}`.

| Path | Request | Response |
|---|---|---|
| `checkrift` / `checksteam` | TokenRequest `{code, user_id, version, ps5}` | CheckResponse `{success, hasAccount, errorCode, message, email, username, isPTR}` |
| `loginrift` / `loginsteam` | TokenRequest | AuthResponse `{success, sessionTicket, authToken, sessionID, playFabId, username, errorCode, message}` |
| `registerrift` / `registersteam` | RegisterRequest `{code, user_id, email, username, password, referrer, ps5}` | AuthResponse |
| `login` | LoginRequest `{username, password, version}` | AuthResponse |
| `linkrift` / `linksteam` | LinkRequest `{code, sessionTicket, user_id, ps5}` | LinkResponse `{success, message, errorCode, linkedToPlatform, linkedToPlatformID}` |
| `updateemail` | `{sessionTicket, email}` | GenericResponse `{success, message, errorCode}` |
| `joinPTR` | `{username, password}` | GenericResponse |

All calls are POST.

## Nexus / Noodle – `https://nexus.zenithmmo.com`

| Path | Request | Response |
|---|---|---|
| `GET /api/status` | – | `{message, loginEnabled}` |
| `GET /api/realm/list` (also `/api/realm/list/<shardTag>`, e.g. `prod-build40`) | Bearer authToken | `{"<id>": {id, name, region, status, load, workerHost, workerPort, tags, version, …}}` – both parsers (DeploymentInfo and RealmListEntry) read the same dictionary |
| `POST /api/realm/join` | NoodleAuthZeuzRequest `{projectId, playFabId, deploymentId, titleId, token}` | NoodleAuthResponse `{projectId, playFabId, queuePosition, spatialPlayerIdentityToken, spatialLoginToken, joinToken, identityToken}` (tokens are JWTs, valid ~5 minutes) |

Other paths **observed in live captures** (formats in the sniffer's `http.log`):

| Path | Content |
|---|---|
| `GET /api/build/<build>` | environment and feature flags (`FULL_BODY_TRACKING`, `REROLL`, …) |
| `GET /api/news/prod` | news list (incl. the shutdown notice) |
| `GET /api/account/prod/preferences` | shard preferences (home shard, recent shards, sort order) |
| `GET /api/account/prod/relations` | `{muted, mutedBy, blocked, reported}` |
| `GET /api/experiment/prod/client-list` | experiment flags, e.g. `{"skip-shard-selection": true}` |
| `GET /api/tiny-content/prod/level_stats/public`, `…/level/public` | level and stat tables per class |

Further paths referenced in the code (stubs in the server): `/api/character/prod/`, `/api/character/prod/list`, `/api/accessCode/`, Miso: `/api/matchmaking/begin|cancel|status`, `/api/deployment/instancelist|endinstance`.

Also fetched: `https://ramenvr.sfo3.digitaloceanspaces.com/builds/ptr_meta_data.json` (build → prod/PTR mapping, shard tag override).

## PlayFab – `https://4bb7b.playfabapi.com`

Standard PlayFab REST (`POST /Client/<Call>`, header `X-Authorization: <SessionTicket>`, response `{code, status, data}`). Which calls are used exactly is shown by the sniffer (`http.log`). Title data for your own server can be placed in `server/ZenithServer/data/titledata.json` (key → string).
