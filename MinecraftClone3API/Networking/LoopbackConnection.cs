using System.Collections.Concurrent;

namespace MinecraftClone3API.Networking
{
    /// <summary>
    /// An in-process connection pair for the integrated (singleplayer) server. Packets are still
    /// serialized to bytes and back so the client and server share no mutable state and the packet
    /// handling path is identical to TCP.
    /// </summary>
    public class LoopbackConnection
    {
        private readonly ConcurrentQueue<byte[]> _clientToServer = new ConcurrentQueue<byte[]>();
        private readonly ConcurrentQueue<byte[]> _serverToClient = new ConcurrentQueue<byte[]>();
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
            private readonly ConcurrentQueue<byte[]> _inbox;
            private readonly ConcurrentQueue<byte[]> _outbox;

            public Endpoint(LoopbackConnection owner, ConcurrentQueue<byte[]> inbox, ConcurrentQueue<byte[]> outbox)
            {
                _owner = owner;
                _inbox = inbox;
                _outbox = outbox;
            }

            public void Send(Packet packet)
            {
                if (!_owner._open) return;
                _outbox.Enqueue(Packet.Serialize(packet));
            }

            public bool TryReceive(out Packet packet)
            {
                if (_inbox.TryDequeue(out var data))
                {
                    packet = Packet.Deserialize(data);
                    return true;
                }

                packet = null;
                return false;
            }

            public bool IsConnected => _owner._open;

            public void Close() => _owner._open = false;
        }
    }
}
