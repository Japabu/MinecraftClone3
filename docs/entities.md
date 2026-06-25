# Entities

Mobs, animals, dropped items, and remote players. Players are **client-authoritative** (see
[architecture.md](architecture.md)); every *other* entity (creatures + items) is **server-authoritative** — the
server owns its position/AI and streams it to clients, which only interpolate and render. Entities are transient:
they live in memory while the server runs and are **not persisted** to disk.

## Model

`Entity` ([Entities/Entity.cs](../MinecraftClone3API/Entities/Entity.cs)) is the shared base: id, `Type`,
`Position`/`Velocity`/`Yaw`/`Pitch`, `OnGround`, a `Dead` flag, the remote-interpolation state, and the
client walk-cycle accumulators (`LimbSwing`/`LimbSwingAmount`, advanced from interpolated motion so no extra
network data is needed). Subclasses:

- **`EntityCreature`** — a walking animal or mob. Server `Update()` runs wander AI (random heading every few
  seconds; a `Hostile` type instead steers toward the nearest player in `SightRange`), then gravity + block
  collision via `EntityPhysics`, hopping 1-block steps. `ServerWorld` is the back-reference its AI reads.
- **`EntityItem`** — a dropped `ItemStack`. Falls under gravity, settles, and despawns after ~5 min; `CanPickup`
  gates a short delay so a just-broken block isn't instantly re-collected.
- **`EntityPlayer`** — the player (its own tuned [PlayerPhysics](../MinecraftClone3API/Entities/PlayerPhysics.cs);
  remote copies are bare `Entity`s with `Type == null`).

`EntityPhysics` ([Entities/EntityPhysics.cs](../MinecraftClone3API/Entities/EntityPhysics.cs)) is the generic
gravity + AABB sweep (clip Y→X→Z against each cell's `GetCollisionBoxes`), parameterised by the entity's
width/height. It runs **server-side only** and reuses the same collision contract as the player.

## Types & registry

An `EntityType` ([Entities/EntityType.cs](../MinecraftClone3API/Entities/EntityType.cs)) is a registered species:
collision `Width`/`Height`, AI fields (`Hostile`, `MoveSpeed`, `MaxHealth`), an `EntityKind` (`Creature`/`Item`),
and the **client-only** render data (`TexturePath` + a GL-free `ModelFactory`). Plugins register them with
`context.Register(EntityType)`; the `EntityRegistry` assigns each a sequential numeric `Id` in registration order.
**Client and server load the same plugins in the same order, so the ids agree on the wire** — the same
block-id-agreement contract. A remote player uses the reserved `EntityType.PlayerTypeId`.

The model description (`EntityModel`/`ModelPart`/`ModelBox`,
[Client/Graphics/EntityModel.cs](../MinecraftClone3API/Client/Graphics/EntityModel.cs)) is pure data — boxes with
texture offsets, authored in the Minecraft mob-model style — so the headless server holds it harmlessly and only
the client ever turns it into GPU buffers.

## Networking

`EntitySpawnPacket` carries `TypeId` (+ an `ItemStack` for items); `EntityMovePacket`/`EntityDespawnPacket` are
unchanged. `WorldServer` owns id allocation (`NextEntityId`, shared with players) and `SpawnEntity`/`DropItem`,
queuing spawns/despawns onto `PendingSpawns`/`PendingDespawns`. Each `Pump`, `ServerNetwork.SyncEntities` drains
those queues (broadcasting spawn/despawn), lets players collect nearby pickup-ready items into their inventory,
and relays every live world entity's position. A joining client is sent a spawn for every existing entity. The
client builds the right subclass in `WorldClient.BuildEntity` and thereafter only interpolates it.

Ambient spawning (`WorldServer.TrySpawnCreatures`) periodically drops a small group of a random creature type on
loaded ground near a random player, up to a soft cap. Breaking a block (`ServerNetwork.ApplyPlaceRequest`)
`DropItem`s the removed block's item form.

**Spawn eggs.** Each creature also registers an `ItemSpawnEgg` (a non-block `Item` with `IsUsable = true`,
holding the `EntityType` to spawn and the official spawn-egg sprite). Right-clicking a usable item sends a
`UseItemRequestPacket` with the targeted cell; the server reads the held item from its own authoritative
inventory copy (so the request can't spoof the item) and calls `Item.OnUseServer`, which spawns the creature.
Fresh players are seeded the spawn eggs on the first hotbar slots (then blocks) so entities are testable at once.

## Rendering

`EntityRenderer` ([Client/Graphics/EntityRenderer.cs](../MinecraftClone3API/Client/Graphics/EntityRenderer.cs))
draws every entity into the deferred **G-buffer** with the `EntityGeometry` shader (real normals + a per-entity
flat light value sampled at its position, so it shades/shadows like the surrounding blocks — see
[rendering.md](rendering.md)). Box models are textured from the **official Minecraft entity sheets**: each box uses
Mojang's texture offsets + dimensions so the sheet maps straight on, with UVs normalized by the texture array's
layer size. (Current Minecraft splits some mob sheets by climate variant, so the paths carry a suffix —
`entity/pig/pig_temperate`, `entity/cow/cow_temperate`, `entity/chicken/chicken_temperate`.) Dropped items render as the spinning, bobbing 3D icon of their block (the same mesh
[ItemIconRenderer](../MinecraftClone3API/Client/Graphics/ItemIconRenderer.cs) uses for the inventory).

Per-type models are built **once** in `EntityRenderer.LoadModels`, which runs in the load flow **after** plugins
register their types and **before** `BlockTextureManager.Upload` — so the entity textures are indexed into the
arrays before they're baked to the GPU. Animation (limb swing keyed by part name `leg*`/`arm*`/`wing*`/`head`,
item spin) is matrix-only at draw time; the shared meshes stay static. Culling is disabled while drawing entities
(box models emit all six faces of each box); depth still resolves visibility.

## Adding an entity

1. Add a box model factory to [EntityModels.cs](../MinecraftClone3API/Client/Graphics/EntityModels.cs) (texels,
   Y-up, feet at 0, facing +Z; name parts `head`/`body`/`leg0..3`/`arm0..1`/`wing0..1` for animation).
2. `context.Register(new EntityType(...))` in your plugin, pointing at the official texture path.
3. Creatures spawn ambiently; spawn explicitly with `world.SpawnEntity(type, pos)`, or register an
   `ItemSpawnEgg(type, spritePath)` for a right-click creative spawn egg.

No on-disk or wire format changes are involved (entities aren't saved), so no world reset is needed.
