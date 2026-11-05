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
//    dotnet run --project tools/ZenithTestClient -- [--nexus URL] [--skywave URL] [--user ID] [--seconds N] [--insecure]
//  Default: http://127.0.0.1 for both.
// =====================================================================================

string nexus = "http://127.0.0.1", skywave = "", user = "testclient-" + Environment.MachineName;
var seconds = 10;
var insecure = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--nexus": nexus = args[++i].TrimEnd('/'); break;
        case "--skywave": skywave = args[++i].TrimEnd('/'); break;
        case "--user": user = args[++i]; break;
        case "--seconds": seconds = int.Parse(args[++i]); break;
        case "--insecure": insecure = true; break;
    }
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
var done = false;
var ok = new HashSet<string>();
var listener = new EventBasedNetListener();
var net = new NetManager(listener) { DisconnectTimeout = 30000 };

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
foreach (var s in new[] { "ProtocolSettings", "InitWorker", "Pong", "EntityQuery", "CreatePlayer:Success" })
    Console.WriteLine($"   [{(ok.Contains(s) ? "OK" : "--")}] {s}");
return ok.Contains("CreatePlayer:Success") ? 0 : 1;

async Task<JsonObject> PostJson(string url, object body)
{
    var resp = await http.PostAsJsonAsync(url, body);
    var text = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"   POST {url} -> {(int)resp.StatusCode}");
    return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
}

static void Step(string s) => Console.WriteLine($"\n== {s}");
static string Short(string s) => s.Length <= 12 ? s : s[..12] + "…";
