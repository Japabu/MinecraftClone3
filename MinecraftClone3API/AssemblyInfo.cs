using System.Runtime.CompilerServices;

// The client renderer was split out of this assembly; it remains a privileged extension that may use
// Core's internals (mesher, chunk codec, registries). This is a one-way grant in the safe direction —
// Core still cannot see the client, and the headless server (a separate assembly without this grant)
// gets nothing.
[assembly: InternalsVisibleTo("MinecraftClone3API.Client")]
