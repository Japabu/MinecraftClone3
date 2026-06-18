namespace MinecraftClone3API.Networking
{
    /// <summary>
    /// A bidirectional packet channel. Implementations frame packets themselves; received packets
    /// are buffered on a background thread and handed out one at a time when the owner pumps
    /// <see cref="TryReceive"/> on its own thread.
    /// </summary>
    public interface IConnection
    {
        void Send(Packet packet);

        /// <summary>Dequeues the next received packet, or returns false if none are pending.</summary>
        bool TryReceive(out Packet packet);

        bool IsConnected { get; }

        void Close();
    }
}
