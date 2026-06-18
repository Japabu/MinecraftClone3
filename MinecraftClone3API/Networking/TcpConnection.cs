using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;

namespace MinecraftClone3API.Networking
{
    /// <summary>
    /// A TCP-backed connection using length-prefixed binary framing. A background thread reads
    /// whole frames into an inbox; <see cref="TryReceive"/> drains it on the owner's thread.
    /// </summary>
    public class TcpConnection : IConnection
    {
        private const int MaxPacketSize = 64 * 1024 * 1024;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly ConcurrentQueue<byte[]> _inbox = new ConcurrentQueue<byte[]>();
        private readonly object _sendLock = new object();
        private readonly Thread _receiveThread;

        private volatile bool _connected = true;

        public TcpConnection(TcpClient client)
        {
            _client = client;
            _client.NoDelay = true;
            _stream = client.GetStream();

            _receiveThread = new Thread(ReceiveLoop) {Name = "TCP Receive", IsBackground = true};
            _receiveThread.Start();
        }

        public void Send(Packet packet)
        {
            var data = Packet.Serialize(packet);
            var lengthPrefix = BitConverter.GetBytes(data.Length);

            lock (_sendLock)
            {
                try
                {
                    _stream.Write(lengthPrefix, 0, lengthPrefix.Length);
                    _stream.Write(data, 0, data.Length);
                }
                catch (Exception)
                {
                    _connected = false;
                }
            }
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

        public bool IsConnected => _connected;

        public void Close()
        {
            _connected = false;
            try
            {
                _client.Close();
            }
            catch (Exception)
            {
                // already torn down
            }
        }

        private void ReceiveLoop()
        {
            var lengthBuffer = new byte[4];

            while (_connected)
            {
                if (!ReadFully(lengthBuffer, 4)) break;

                var length = BitConverter.ToInt32(lengthBuffer, 0);
                if (length <= 0 || length > MaxPacketSize) break;

                var data = new byte[length];
                if (!ReadFully(data, length)) break;

                _inbox.Enqueue(data);
            }

            _connected = false;
        }

        private bool ReadFully(byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                int read;
                try
                {
                    read = _stream.Read(buffer, offset, count - offset);
                }
                catch (Exception)
                {
                    return false;
                }

                if (read <= 0) return false;
                offset += read;
            }

            return true;
        }
    }
}
