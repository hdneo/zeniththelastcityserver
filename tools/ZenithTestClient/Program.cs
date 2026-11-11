using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiteNetLib;
using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

// =====================================================================================
//  ZenithTestClient – replays the connection flow of the real client:
//    1. Skywave  POST {skywave}/loginrift            -> sessionTicket, authToken, playFabId
//    2. Nexus    GET  {nexus}/api/realm/list         -> workerHost/workerPort
//    3. Nexus    POST {nexus}/api/realm/join         -> joinToken, identityToken
//    4. UDP      LiteNetLib Connect(key = joinToken)
//               <- ProtocolSettings (RSA)  -> AuthWorker (encrypted key) <- InitWorker
//    5. EntityQuery(PlayerCreator) -> CommandRequest PlayerCreator.CreatePlayer
//
//  Usage:
//    dotnet run --project tools/ZenithTestClient -- [--nexus URL] [--skywave URL] [--user ID] [--seconds N] [--character NAME [--spawn|--challenges|--shop]] [--insecure]
//  Default: http://127.0.0.1 for both.
// =====================================================================================

string nexus = "http://127.0.0.1", skywave = "", user = "testclient-" + Environment.MachineName;
var seconds = 10;
var insecure = false;
var spawn = false;
var challenges = false;
var shop = false;
string? characterName = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--nexus": nexus = args[++i].TrimEnd('/'); break;
        case "--skywave": skywave = args[++i].TrimEnd('/'); break;
        case "--user": user = args[++i]; break;
        case "--seconds": seconds = int.Parse(args[++i]); break;
        case "--character": characterName = args[++i]; break;
        case "--spawn": spawn = true; break;
        case "--challenges": spawn = challenges = true; break;
        case "--shop": spawn = challenges = shop = true; break;
        case "--insecure": insecure = true; break;
    }
}
if (spawn && characterName is null)
{
    Console.Error.WriteLine("--spawn, --challenges, and --shop require --character NAME");
    return 2;
}
if (skywave == "") skywave = nexus + "/api/game";

var handler = new HttpClientHandler();
if (insecure) handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
http.DefaultRequestHeaders.Add("X-Zenith-Client-Version", "testclient");

// ---- 1. Skywave ----
Step("Skywave loginrift");
http.DefaultRequestHeaders.Authorization = new("Bearer", "be4ba5c7-4314-48c6-8b97-9991cae08c51");
var login = await PostJson($"{skywave}/loginrift", new { code = "testclient", user_id = user, version = "testclient", ps5 = false });
var authToken = login["authToken"]?.ToString() ?? "";
var sessionTicket = login["sessionTicket"]?.ToString() ?? "";
var playFabId = login["playFabId"]?.ToString() ?? "";
Console.WriteLine($"   playFabId={playFabId} success={login["success"]}");

// ---- 2. Realm list ----
Step("Realm list");
http.DefaultRequestHeaders.Authorization = new("Bearer", authToken);
var realms = JsonNode.Parse(await http.GetStringAsync($"{nexus}/api/realm/list"))!.AsObject();
var (realmId, realm) = realms.First();
var host = realm!["workerHost"]!.ToString();
var port = realm["workerPort"]!.GetValue<int>();
Console.WriteLine($"   {realms.Count} realm(s), using '{realmId}' -> {host}:{port}");

// ---- 3. Join ----
Step("Realm join");
var join = await PostJson($"{nexus}/api/realm/join", new { projectId = "beta_mexico_eight_889", playFabId, deploymentId = realmId, titleId = "4BB7B", token = sessionTicket });
var joinToken = join["joinToken"]?.ToString() ?? "";
var identityToken = join["identityToken"]?.ToString() ?? "";
Console.WriteLine($"   joinToken={Short(joinToken)} identityToken={Short(identityToken)}");

// ---- 4. ZenFabric ----
Step($"ZenFabric UDP {host}:{port}");
byte[]? key = null;
var reqId = 1L;
long playerCreator = 0;
long playerEntity = 0;
var done = false;
var ok = new HashSet<string>();
var listener = new EventBasedNetListener();
var net = new NetManager(listener) { DisconnectTimeout = 30000 };
var characterCommands = characterName is null ? null : new CharacterScenario(playFabId, characterName, challenges ? 4u : 3u);
SpawnScenario? spawnScenario = null;

void Send(NetPeer p, ZfOp op)
{
    Console.WriteLine($"   -> {OpDescriber.Summary(op)}");
    p.Send(ZfCodec.Frame(op, key), DeliveryMethod.ReliableOrdered);
}

listener.PeerConnectedEvent += p => Console.WriteLine($"   connected to {p.EndPoint}");
listener.PeerDisconnectedEvent += (p, info) => { Console.WriteLine($"   disconnected: {info.Reason}"); done = true; };
listener.NetworkReceiveEvent += (peer, reader, ch, dm) =>
{
    var payload = ZfCodec.Unframe(reader.GetRemainingBytes(), key);
    reader.Recycle();
    var op = ZfOps.Parse(payload);
    Console.WriteLine($"   <- {OpDescriber.Summary(op)}");
    if (spawnScenario is not null)
    {
        var nextSpawnOp = spawnScenario.Handle(op, reqId);
        if (nextSpawnOp is not null)
        {
            reqId++;
            Send(peer, nextSpawnOp);
        }
        if (spawnScenario.Completed)
        {
            ok.Add("CharacterSpawn");
            done = true;
        }
        return;
    }
    switch (op)
    {
        case ProtocolSettingsOp ps:
            ok.Add("ProtocolSettings");
            var newKey = ZfCodec.NewSymmetricKey();
            Send(peer, new AuthWorkerOp { IdentityToken = identityToken, SymmetricKeyParts = ZfCodec.EncryptSymmetricKey(newKey, ps.Modulus, ps.Exponent) });
            key = newKey; // encrypted from now on (like Connection.OnNetworkReceive in the client)
            break;
        case InitWorkerOp:
            ok.Add("InitWorker");
            Send(peer, new PingOp { RequestId = reqId++ });
            // QueryConstraint with component_constraint = PlayerCreator (13000)
            var q = new SchemaWriter();
            for (uint f = 1; f <= 7; f++) q.AddEmptyOption(f);
            q.AddUInt32(8, Ids.PlayerCreator, isOption: true);
            q.AddEmptyList(9); q.AddEmptyList(10); q.AddEmptyOption(11); q.AddEmptyOption(12);
            Send(peer, new EntityQueryRequestOp { RequestId = reqId++, SerializedConstraint = q.ToArray() });
            break;
        case PongOp: ok.Add("Pong"); break;
        case EntityQueryResponseOp r:
            ok.Add("EntityQuery");
            playerCreator = r.Results.FirstOrDefault()?.EntityId ?? 0;
            if (playerCreator != 0)
            {
                var a = new SchemaWriter();
                a.AddBytes(1, "testclient"u8.ToArray());
                Send(peer, new CommandRequestOp { RequestId = reqId++, EntityId = playerCreator, ComponentId = Ids.PlayerCreator, CommandIndex = Ids.CreatePlayerCommandIndex, Data = a.ToArray() });
            }
            break;
        case CommandResponseOp cr when cr.ComponentId == Ids.PlayerCreator:
            ok.Add("CreatePlayer:" + cr.Status);
            if (cr.Status == StatusCode.Success && characterCommands is not null)
            {
                playerEntity = ReadCreatedPlayer(cr.Data);
                Send(peer, characterCommands.ValidateName(reqId++));
            }
            break;
        case CommandResponseOp cr when cr.ComponentId == Ids.CharacterSpawnerComponent && characterCommands is not null:
            var next = characterCommands.Handle(cr, reqId++);
            if (next is not null) Send(peer, next);
            if (characterCommands.Completed)
            {
                ok.Add("CharacterRoster");
                if (spawn)
                {
                    spawnScenario = new SpawnScenario(playFabId, sessionTicket, characterCommands.CharacterId!, characterName!, playerEntity, challenges, shop);
                    Send(peer, spawnScenario.Start(reqId++));
                }
                else done = true;
            }
            break;
    }
};

net.Start();
net.Connect(host, port, joinToken);
var until = DateTime.UtcNow.AddSeconds(seconds);
while (!done && DateTime.UtcNow < until)
{
    net.PollEvents();
    Thread.Sleep(5);
}
net.Stop();

Step("Result");
var expected = new List<string> { "ProtocolSettings", "InitWorker", "Pong", "EntityQuery", "CreatePlayer:Success" };
if (characterCommands is not null) expected.Add("CharacterRoster");
if (spawn) expected.Add("CharacterSpawn");
foreach (var s in expected)
    Console.WriteLine($"   [{(ok.Contains(s) ? "OK" : "--")}] {s}");
return expected.All(ok.Contains) ? 0 : 1;

async Task<JsonObject> PostJson(string url, object body)
{
    var resp = await http.PostAsJsonAsync(url, body);
    var text = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"   POST {url} -> {(int)resp.StatusCode}");
    return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
}

static void Step(string s) => Console.WriteLine($"\n== {s}");
static string Short(string s) => s.Length <= 12 ? s : s[..12] + "…";

static long ReadCreatedPlayer(byte[] data)
{
    var reader = new SchemaReader(data);
    if (reader.ReadFieldId() != 1) throw new InvalidDataException("Missing created player ID");
    return reader.ReadInt64();
}
