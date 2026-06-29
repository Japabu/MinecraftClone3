using System.Collections.Concurrent;

namespace MinecraftClone3API.Networking
{
    /// <summary>
    /// An in-process connection pair for the integrated (singleplayer) server. Packets are passed by
    /// reference, not serialized to bytes and back: both endpoints are pumped sequentially on the
    /// client's main thread (server <see cref="ServerNetwork.Pump"/> produces, then client
    /// <see cref="MinecraftClone3API.Client.Blocks.WorldClient.Update"/> consumes, in the same
    /// <c>StateWorld.Update</c>), and the server builds a fresh packet per <see cref="Send"/> and never
    /// reads it back — so there is no shared mutable state to race on. This skips the per-packet
    /// <see cref="System.IO.MemoryStream"/> + <c>ToArray</c> round trip that serialization would otherwise
    /// require on the singleplayer main thread. The TCP path still serializes (see <see cref="TcpConnection"/>);
    /// only the loopback shortcuts it, so the wire packets themselves are unchanged.
    /// </summary>
    public class LoopbackConnection
    {
        private readonly ConcurrentQueue<Packet> _clientToServer = new ConcurrentQueue<Packet>();
        private readonly ConcurrentQueue<Packet> _serverToClient = new ConcurrentQueue<Packet>();
        private bool _open = true;

        public IConnection ClientSide { get; }
        public IConnection ServerSide { get; }

        public LoopbackConnection()
        {
            ClientSide = new Endpoint(this, _serverToClient, _clientToServer);
            ServerSide = new Endpoint(this, _clientToServer, _serverToClient);
        }

        private class Endpoint : IConnection
        {
            private readonly LoopbackConnection _owner;
            private readonly ConcurrentQueue<Packet> _inbox;
            private readonly ConcurrentQueue<Packet> _outbox;

            public Endpoint(LoopbackConnection owner, ConcurrentQueue<Packet> inbox, ConcurrentQueue<Packet> outbox)
            {
                _owner = owner;
                _inbox = inbox;
                _outbox = outbox;
            }

            public void Send(Packet packet)
            {
                if (!_owner._open) return;
                _outbox.Enqueue(packet);
            }

            public bool TryReceive(out Packet packet)
            {
                if (_inbox.TryDequeue(out packet))
                    return true;

                packet = null;
                return false;
            }

            public bool IsConnected => _owner._open;

            public void Close() => _owner._open = false;
        }
    }
}
