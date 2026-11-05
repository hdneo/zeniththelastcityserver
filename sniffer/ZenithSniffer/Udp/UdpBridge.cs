using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LiteNetLib;
using Zenith.Protocol.ZenFabric;
using ZenithSniffer.Capture;
using ZenithSniffer.Net;

namespace ZenithSniffer.Udp;

/// <summary>
/// Man-in-the-middle for ZenFabric (LiteNetLib/UDP).
///
///   game client  <==UDP==>  bridge (local port)  <==UDP==>  real ZenFabric server
///
/// Flow per connection:
///  1. Client connects (key = joinToken). The bridge connects upstream with the same key.
///  2. The bridge sends the client its OWN ProtocolSettings (own RSA key).
///  3. Client sends AuthWorker (symmetric key encrypted with the bridge RSA key) -> bridge decrypts the key.
///  4. Once the upstream ProtocolSettings arrive: the bridge sends AuthWorker with the SAME key,
///     encrypted with the upstream RSA key.
///  5. From then on both sides use the same XOR key -> all packets are forwarded unchanged
///     and only decrypted for logging.
/// </summary>
public sealed class UdpBridgeManager : IDisposable
{
    private readonly SnifferOptions _opts;
    private readonly CaptureSession _capture;
    private readonly UpstreamDns _dns;
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly object _lock = new();
    private readonly Dictionary<string, Bridge> _bridges = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NetManager> _managers = new();
    private readonly Thread _thread;
    private volatile bool _running = true;
    private int _nextPort;
    private int _nextSession;

    public UdpBridgeManager(SnifferOptions opts, CaptureSession capture, UpstreamDns dns)
    {
        _opts = opts;
        _capture = capture;
        _dns = dns;
        _nextPort = opts.UdpPortBase;
        _thread = new Thread(Loop) { IsBackground = true, Name = "UdpBridge" };
        _thread.Start();
    }

    /// <summary>Returns (or starts) the local bridge for an upstream endpoint. Returns the local port.</summary>
    public int GetOrCreateBridge(string upstreamHost, int upstreamPort)
    {
        var key = $"{upstreamHost}:{upstreamPort}";
        lock (_lock)
        {
            if (_bridges.TryGetValue(key, out var b)) return b.LocalPort;
            var ip = _dns.ResolveAsync(upstreamHost).GetAwaiter().GetResult();
            b = new Bridge(this, new IPEndPoint(ip, upstreamPort), upstreamHost);
            var bind = IPAddress.Parse(_opts.BindAddress);
            while (!b.Net.Start(bind, IPAddress.IPv6Any, _nextPort)) _nextPort++;
            b.LocalPort = _nextPort++;
            _bridges[key] = b;
            _managers.Add(b.Net);
            Console.WriteLine($"[UDP] Bridge {_opts.AdvertiseHost}:{b.LocalPort}  ->  {upstreamHost} ({ip}):{upstreamPort}");
            _capture.LogSession(new { t = DateTimeOffset.UtcNow, evt = "bridge", local = b.LocalPort, upstreamHost, upstreamIp = ip.ToString(), upstreamPort });
            return b.LocalPort;
        }
    }

    private void Register(NetManager m) { lock (_lock) _managers.Add(m); }
    private void Unregister(NetManager m) { lock (_lock) _managers.Remove(m); }

    private void Loop()
    {
        while (_running)
        {
            NetManager[] snapshot;
            lock (_lock) snapshot = _managers.ToArray();
            foreach (var m in snapshot)
            {
                try { m.PollEvents(); }
                catch (Exception ex) { Console.WriteLine("[UDP] Error: " + ex.Message); }
            }
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(1000);
        lock (_lock) foreach (var m in _managers) m.Stop();
    }

    // =====================================================================

    private sealed class Bridge : INetEventListener
    {
        private readonly UdpBridgeManager _mgr;
        private readonly Dictionary<int, Session> _sessions = new();
        public IPEndPoint Upstream { get; }
        public string UpstreamHost { get; }
        public NetManager Net { get; }
        public int LocalPort { get; set; }

        public Bridge(UdpBridgeManager mgr, IPEndPoint upstream, string upstreamHost)
        {
            _mgr = mgr;
            Upstream = upstream;
            UpstreamHost = upstreamHost;
            Net = new NetManager(this) { DisconnectTimeout = 30000, AutoRecycle = true };
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            string joinToken;
            try { joinToken = request.Data.GetString(); } catch { joinToken = ""; }
            var peer = request.Accept();
            var s = new Session(_mgr, this, peer, joinToken, Interlocked.Increment(ref _mgr._nextSession));
            _sessions[peer.Id] = s;
            s.ConnectUpstream();
        }

        public void OnPeerConnected(NetPeer peer)
        {
            if (_sessions.TryGetValue(peer.Id, out var s)) s.OnClientConnected();
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (_sessions.Remove(peer.Id, out var s)) s.OnClientDisconnected(disconnectInfo.Reason.ToString());
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (_sessions.TryGetValue(peer.Id, out var s)) s.FromClient(reader.GetRemainingBytes(), channelNumber, deliveryMethod);
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    }

    private sealed class Session : INetEventListener
    {
        private readonly UdpBridgeManager _mgr;
        private readonly Bridge _bridge;
        private readonly NetPeer _client;
        private readonly string _joinToken;
        private readonly int _id;
        private readonly NetManager _upNet;
        private NetPeer? _upstream;
        private byte[]? _key;
        private AuthWorkerOp? _clientAuth;
        private ProtocolSettingsOp? _upstreamSettings;
        private bool _authForwarded;
        private readonly Queue<(byte[] Data, byte Channel, DeliveryMethod Method)> _toUpstream = new();

        public Session(UdpBridgeManager mgr, Bridge bridge, NetPeer client, string joinToken, int id)
        {
            _mgr = mgr;
            _bridge = bridge;
            _client = client;
            _joinToken = joinToken;
            _id = id;
            _upNet = new NetManager(this) { DisconnectTimeout = 30000, AutoRecycle = true };
        }

        private CaptureSession Cap => _mgr._capture;

        public void ConnectUpstream()
        {
            _upNet.Start();
            _mgr.Register(_upNet);
            _upstream = _upNet.Connect(_bridge.Upstream, _joinToken);
            Cap.RegisterEndpoints(_id, _client.EndPoint, _bridge.Upstream);
            Console.WriteLine($"[UDP {_id}] Client {_client.EndPoint} connected, connecting to {_bridge.UpstreamHost}:{_bridge.Upstream.Port}");
            Cap.LogSession(new { t = DateTimeOffset.UtcNow, evt = "connect", session = _id, client = _client.EndPoint.ToString(), upstream = _bridge.Upstream.ToString(), joinToken = _joinToken });
        }

        public void OnClientConnected()
        {
            // Our own ProtocolSettings, so we can read the symmetric key.
            var ps = ZfCodec.MakeProtocolSettings(_mgr._rsa);
            _client.Send(ZfCodec.Frame(ps, null), DeliveryMethod.ReliableOrdered);
            Cap.LogZenFabric(Direction.ServerToClient, _id, ps.Serialize(), "generated by bridge (own RSA key)");
        }

        public void OnClientDisconnected(string reason)
        {
            Console.WriteLine($"[UDP {_id}] Client disconnected ({reason})");
            Cap.LogSession(new { t = DateTimeOffset.UtcNow, evt = "client-disconnect", session = _id, reason });
            _upstream?.Disconnect();
            _mgr.Unregister(_upNet);
            _upNet.Stop();
        }

        public void FromClient(byte[] data, byte channel, DeliveryMethod method)
        {
            if (_key is null)
            {
                // Expected: AuthWorker (plain text)
                byte[] payload;
                try { payload = ZfCodec.Unframe(data, null); }
                catch (Exception ex) { Console.WriteLine($"[UDP {_id}] invalid frame from client: {ex.Message}"); return; }
                if (ZfOps.TryParse(payload, out var op, out _) && op is AuthWorkerOp auth)
                {
                    _key = ZfCodec.DecryptSymmetricKey(auth.SymmetricKeyParts, _mgr._rsa);
                    _clientAuth = auth;
                    Cap.LogZenFabric(Direction.ClientToServer, _id, payload, "original (key encrypted with bridge RSA key)");
                    Cap.LogSession(new { t = DateTimeOffset.UtcNow, evt = "key", session = _id, identityToken = auth.IdentityToken, symmetricKeyHex = Convert.ToHexString(_key) });
                    Console.WriteLine($"[UDP {_id}] Symmetric key received ({_key.Length} bytes)");
                    TryForwardAuth();
                    return;
                }
                // Unencrypted message before the handshake: just forward/buffer it
                Cap.LogZenFabric(Direction.ClientToServer, _id, payload, "before handshake");
                Enqueue(data, channel, method);
                return;
            }

            try { Cap.LogZenFabric(Direction.ClientToServer, _id, ZfCodec.Unframe(data, _key)); }
            catch (Exception ex) { Console.WriteLine($"[UDP {_id}] C->S not decodable: {ex.Message}"); }
            Enqueue(data, channel, method);
        }

        private void Enqueue(byte[] data, byte channel, DeliveryMethod method)
        {
            if (_authForwarded && _upstream is { ConnectionState: ConnectionState.Connected })
                _upstream.Send(data, channel, method);
            else
                _toUpstream.Enqueue((data, channel, method));
        }

        private void TryForwardAuth()
        {
            if (_authForwarded || _key is null || _clientAuth is null || _upstreamSettings is null || _upstream is null) return;
            var auth = new AuthWorkerOp
            {
                IdentityToken = _clientAuth.IdentityToken,
                SymmetricKeyParts = ZfCodec.EncryptSymmetricKey(_key, _upstreamSettings.Modulus, _upstreamSettings.Exponent),
            };
            _upstream.Send(ZfCodec.Frame(auth, null), DeliveryMethod.ReliableOrdered);
            _authForwarded = true;
            Console.WriteLine($"[UDP {_id}] AuthWorker forwarded upstream – session running");
            while (_toUpstream.TryDequeue(out var m)) _upstream.Send(m.Data, m.Channel, m.Method);
        }

        // ---- Upstream side (INetEventListener of the upstream client) ----

        public void OnPeerConnected(NetPeer peer) =>
            Console.WriteLine($"[UDP {_id}] Upstream connected ({peer.EndPoint})");

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            Console.WriteLine($"[UDP {_id}] Upstream disconnected ({disconnectInfo.Reason})");
            Cap.LogSession(new { t = DateTimeOffset.UtcNow, evt = "upstream-disconnect", session = _id, reason = disconnectInfo.Reason.ToString() });
            _client.Disconnect();
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            var data = reader.GetRemainingBytes();
            if (!_authForwarded)
            {
                byte[] payload;
                try { payload = ZfCodec.Unframe(data, null); }
                catch (Exception ex) { Console.WriteLine($"[UDP {_id}] invalid frame from upstream: {ex.Message}"); return; }
                if (ZfOps.TryParse(payload, out var op, out _) && op is ProtocolSettingsOp ps)
                {
                    _upstreamSettings = ps;
                    Cap.LogZenFabric(Direction.ServerToClient, _id, payload, "original from server (not forwarded to client)");
                    TryForwardAuth();
                    return;
                }
                Cap.LogZenFabric(Direction.ServerToClient, _id, payload, "before handshake");
                _client.Send(data, channelNumber, deliveryMethod);
                return;
            }
            try { Cap.LogZenFabric(Direction.ServerToClient, _id, ZfCodec.Unframe(data, _key)); }
            catch (Exception ex) { Console.WriteLine($"[UDP {_id}] S->C not decodable: {ex.Message}"); }
            _client.Send(data, channelNumber, deliveryMethod);
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
            Console.WriteLine($"[UDP {_id}] Upstream network error {socketError}");
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        public void OnConnectionRequest(ConnectionRequest request) => request.Reject();
    }
}
