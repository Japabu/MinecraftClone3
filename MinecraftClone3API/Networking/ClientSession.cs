using System.Collections.Generic;
using MinecraftClone3API.Entities;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Networking
{
    /// <summary>Server-side state for one connected client.</summary>
    public class ClientSession
    {
        public readonly IConnection Connection;
        public readonly HashSet<Vector3i> SentChunks = new HashSet<Vector3i>();

        public EntityPlayer Player;
        public int EntityId;
        public bool LoggedIn;

        public ClientSession(IConnection connection)
        {
            Connection = connection;
        }
    }
}
