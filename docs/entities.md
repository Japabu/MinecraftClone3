# Entities

Mobs, animals, dropped items, and remote players. Players are **client-authoritative** (see
[architecture.md](architecture.md)); every *other* entity (creatures + items) is **server-authoritative** — the
server owns its position/AI and streams it to clients, which only interpolate and render.

**Entities persist with their chunk.** Each world entity belongs to the chunk containing its position;
`EntitySerializer` writes it (type + transform + subclass state, all by stable registry **name**) into a
per-chunk blob in a second `RegionStore` (`.rei`/`.red`, parallel to the chunk `.ri`/`.rd`). Saving and
respawning run on the **tick thread** at the chunk load/unload drains (`WorldServer.SpawnSavedEntities` /
`SaveAndDespawnChunkEntities`), so all `Entities` mutation stays single-threaded; the disk read happens off the
tick thread on the load thread. Entities exist on disk **or** in the live world, never both, so a reload never
duplicates them. The player is persisted separately (inventory + stats + position/look) by
[PlayerSerializer](../MinecraftClone3API/IO/PlayerSerializer.cs). Entity persistence runs on chunk unload and
clean shutdown (not the periodic chunk autosave), so a hard crash can lose entity motion since the last unload.

## Model

`Entity` ([Entities/Entity.cs](../MinecraftClone3API/Entities/Entity.cs)) is the shared base: id, `Type`,
`Position`/`Velocity`/`Yaw`/`Pitch`, `OnGround`, a `Dead` flag, the remote-interpolation state, and the
client walk-cycle accumulators (`LimbSwing`/`LimbSwingAmount`, advanced from interpolated motion so no extra
network data is needed). Subclasses:

- **`EntityCreature`** — a walking animal or mob. Server `Update()` runs wander AI (random heading every few
  seconds; a `Hostile` type instead steers toward the nearest player in `SightRange`; a `NeutralUntilProvoked`
  type — the enderman — wanders until a player's look ray falls within a tight cone of its head, then latches
  onto and chases that player until they drift past `LoseRange`, looking away no longer calming it), then
  gravity + block collision via `EntityPhysics`, hopping 1-block steps. `ServerWorld` is the back-reference its
  AI reads. Carries combat state: `Health` (lazily seeded to `Type.MaxHealth` on the first tick) and a
  `HurtCooldown` invuln counter, both mutated by `EntityCombat`. A `Hostile` type with `Type.AttackDamage > 0`
  also deals melee **contact damage** to a player in range on its ~1 s attack cadence (`TryAttack` →
  `PlayerSurvival.ApplyContactDamage`).

- **`EntityItem`** — a dropped `ItemStack`. Falls under gravity, settles, and despawns after ~5 min; `CanPickup`
  gates a short delay so a just-broken block isn't instantly re-collected.
- **`EntityFallingBlock`** — a block (sand, gravel) mid-fall. Spawned by `BlockFalling` via
  `WorldServer.SpawnFallingBlock` when the block is unsupported (see [world-model.md](world-model.md)); falls via
  `EntityPhysics` and on landing turns back into a placed block at its rest cell (stacking one cell up if that
  cell was filled the same tick). `Position` is the block's bottom-centre. It carries its block id on a
  `FallingBlockData` so the client renders the right full-size block (`EntityRenderer.DrawFallingBlock`, reusing
  the dropped-item block mesh at full scale, no spin).
- **`EntityProjectile`** — a thrown ender pearl. Flies under light gravity with a **sub-stepped** swept block
  check (so a fast pearl can't tunnel a thin wall); on entering a solid collision box it queues a teleport of
  its thrower (`OwnerId`) onto `WorldServer.PendingTeleports` and dies. Rendered client-side as a single
  **camera-facing billboard** quad of the pearl item sprite (`EntityRenderer.DrawProjectile`, oriented from the
  camera basis), not a box model. Thrown by `ItemEnderPearl.OnUseServer` along the player's look vector.
- **`EntityPlayer`** — the player (its own tuned [PlayerPhysics](../MinecraftClone3API/Entities/PlayerPhysics.cs);
  remote copies are bare `Entity`s with `Type == null`). Also carries the survival stats
  (`Health`/`Hunger`/`Saturation`/`Exhaustion`/`Air`/`GameMode`, see below) — server-authoritative, mirrored on
  the client from the stats packet.

`EntityPhysics` ([Entities/EntityPhysics.cs](../MinecraftClone3API/Entities/EntityPhysics.cs)) is the generic
gravity + AABB sweep (clip Y→X→Z against each cell's `GetCollisionBoxes`), parameterised by the entity's
width/height. It runs **server-side only** and reuses the same collision contract as the player.

## Types & registry

An `EntityType` ([Entities/EntityType.cs](../MinecraftClone3API/Entities/EntityType.cs)) is a registered species:
collision `Width`/`Height`, AI fields (`Hostile`, `MoveSpeed`, `MaxHealth`), combat fields (`AttackDamage` for a
hostile mob's contact damage, an optional `LootTable` rolled on death), an `EntityKind`
(`Creature`/`Item`/`FallingBlock`),
and the **client-only** render data (`TexturePath` + a `ModelPath` resource key). Plugins register them with
`context.Register(EntityType)`; the `EntityRegistry` assigns each a sequential numeric `Id` in registration order.
**Client and server load the same plugins in the same order, so the ids agree on the wire** — the same
block-id-agreement contract. A remote player uses the reserved `EntityType.PlayerTypeId`.

The in-memory model (`EntityModel`/`ModelPart`/`ModelBox`,
[Client/Graphics/EntityModel.cs](../MinecraftClone3API/Client/Graphics/EntityModel.cs)) is GL-free data — a flat
list of parts, each a group of texture-offset boxes that pivots for animation. The server never builds it; only
the client loads it (from a data file — see Rendering) and turns it into GPU buffers.

**Per-entity state (`EntityData`).** Mob-specific state lives in one nullable `Entity.Data` slot — the entity
analog of `BlockData` (see [world-model.md](world-model.md)): an abstract `EntityData`
([Entities/EntityData.cs](../MinecraftClone3API/Entities/EntityData.cs)) with `Serialize`/`Deserialize`, concrete
subclasses (`SheepData { Sheared }`) registered by type (`PluginContext.RegisterEntityData<T>`) and (de)serialized
behind a registry-key tag. An `EntityType.DataFactory` builds the instance a creature spawns with; it rides
`EntitySpawnPacket`, changes broadcast via `EntityDataPacket`, and a creature persists its `Data` through
`EntitySerializer` (so a name-based `Data` like `SheepData` survives — but a `Data` that embeds ids must store
them by name, as `FallingBlockData` does on the disk path via the falling block's `SerializeState`). The
base class is GL-free; `EntityData.OverlayVisible` lets it drive the renderer (hide a sheared sheep's wool)
without the API knowing the concrete subclass. The base `Entity` stays free of per-mob flags.

## Networking

`EntitySpawnPacket` carries `TypeId` (+ an `ItemStack` for items); `EntityMovePacket` also carries `HurtTime`
(damage flash) and `HeldItemId` (the player's main-hand item, so others can see what's held — a session-local id,
safe on the live wire as the client and server share the plugin load); `EntityDespawnPacket` is unchanged.
`WorldServer` owns id allocation (`NextEntityId`, shared with players) and `SpawnEntity`/`DropItem`,
queuing spawns/despawns onto `PendingSpawns`/`PendingDespawns`. Each `Pump`, `ServerNetwork.SyncEntities` drains
those queues (broadcasting spawn/despawn), lets players collect nearby pickup-ready items into their inventory,
and relays every live world entity's position. A joining client is sent a spawn for every existing entity. The
client builds the right subclass in `WorldClient.BuildEntity` and thereafter only interpolates it.

Ambient spawning (`WorldServer.TrySpawnCreatures`) periodically drops a small group of a random creature type on
loaded ground near a random player, up to a soft cap. Breaking a block (`ServerNetwork.ApplyPlaceRequest`)
`DropItem`s the removed block's item form. **Player drops** (the Drop keybind, default Q; Ctrl+Q for the whole
stack) go through `DropItemRequest` → `ServerNetwork.ApplyDropRequest`, which throws the item along the player's
look direction with `EntityItem.PickupDelay` raised to ~2 s so it doesn't fly straight back into the thrower
(block-break drops keep the short ~0.5 s default).

**Spawn eggs.** Each creature also registers an `ItemSpawnEgg` (a non-block `Item` with `IsUsable = true`,
holding the `EntityType` to spawn and the official spawn-egg sprite). Right-clicking a usable item sends a
`UseItemRequestPacket` with the targeted cell; the server reads the held item from its own authoritative
inventory copy (so the request can't spoof the item) and calls `Item.OnUseServer`, which spawns the creature.
Fresh players are seeded the spawn eggs on the first hotbar slots (then blocks) so entities are testable at once.

**Thrown items (ender pearl).** An `Item` with `RequiresBlockTarget == false` (the ender pearl) fires its
`OnUseServer` even when the crosshair is on no block (aiming at the sky), so `PlayerController` sends a
`UseItemRequest` with a dummy cell the action ignores. `ItemEnderPearl` spawns an `EntityProjectile` at the
player's eye along their look vector, tagged with the thrower's `EntityId`. When it lands, the projectile queues
`(ownerId, position)` on `WorldServer.PendingTeleports`; `ServerNetwork.SyncEntities` drains it, sends the owner
a **`PlayerTeleportPacket`**, mirrors the position on the server copy, and applies 5 points of pearl fall damage
in survival. The player is position-authoritative, so this packet is the only relocation outside the respawn
snap: the client obeys by snapping its local player there and clearing its fall accumulator
(`StateWorld.UpdateTeleport` → `PlayerController.ResetFall`).

**Item use on an entity (shears).** An `Item` with `UsableOnEntity = true` (the shears) takes precedence on
right-click: `PlayerController.PickEntity` ray-casts the held look direction against each entity's collision AABB
(nearer than the targeted block), and a hit sends `UseItemOnEntityRequestPacket` with the entity id. The server
resolves the id against its **own** `WorldServer.FindEntity` list (so it can't act on an arbitrary id), runs
`Item.OnUseOnEntity`, then broadcasts the entity's (possibly changed) `EntityData`. `ItemShears.OnUseOnEntity`
shears a woolly sheep — flips `SheepData.Sheared` (which the client uses to drop the wool layer) and `DropItem`s
1–3 wool.

**Melee combat (attacking mobs).** A fresh left-click reuses the same `PlayerController.PickEntity` raycast: if a
creature is hit (nearer than the targeted block) the click is an **attack**, not a block break — it sends
`AttackEntityRequestPacket` with the entity id and swallows the rest of that hold so it doesn't also mine. The
server resolves the id against its own `FindEntity` list and runs `EntityCombat.DamageEntity` with the held
item's `Item.AttackDamage` (bare hand = `EntityCombat.BaseHandDamage` = 1; swords raise it). `EntityCombat`
([Entities/EntityCombat.cs](../MinecraftClone3API/Entities/EntityCombat.cs)) is the GL-free, stateless server
combat sink: it gates on the target's `HurtCooldown` (0.5 s invuln), subtracts `Health`, sets a `HurtTime`
flash timer (streamed in `EntityMovePacket`; the client renders the model red while it runs — see
[rendering.md](rendering.md)), applies horizontal + upward knockback to the target's `Velocity`, and on death
rolls the type's `LootTable`
([Entities/LootTable.cs](../MinecraftClone3API/Entities/LootTable.cs)) — `DropItem`-ing each stack — before
setting `Dead`. Death/despawn then streams through the existing `PendingDespawns` path, so **no new server→client
packet is needed**. Loot is declared on the `EntityType` by item registry key (resolved lazily, like the shears'
wool), e.g. zombie → rotten flesh, cow → beef + leather.

## Player survival

Health/hunger/damage are **server-authoritative** even though the player's *position* is client-authoritative.
`PlayerSurvival` ([Entities/PlayerSurvival.cs](../MinecraftClone3API/Entities/PlayerSurvival.cs)) is stateless
logic over `EntityPlayer`'s stat fields, run once per 20 tps tick from `WorldServer.Update`'s player loop. It
applies Minecraft-exact mechanics on Normal difficulty:

- **Health** 20 (10 hearts, 1 heart = 2 points), **Hunger** 20, saturation gated by hunger.
- **Hunger/saturation/exhaustion:** movement (approximated server-side from the reported position delta) and
  regen accrue exhaustion; at the threshold it drains saturation, then hunger.
- **Regen:** hunger ≥ 18 heals 1 health on the 80-tick cadence (costs exhaustion). **Starvation:** hunger 0
  drains 1 health on the same cadence, floored at 1 (Normal).
- **Drowning:** when the head cell is liquid, `Air` drains from 300; at 0, 2 points every 20 ticks (refills
  above water). **Void:** below Y −96, 4 points every 10 ticks.
- **Fall damage:** `max(0, ceil(distance − 3))`. Because the client owns position, `PlayerController` accumulates
  the fall while airborne and reports it on landing (`PlayerFallPacket`); the server computes the damage.
- **Creative** short-circuits everything (stats clamped full, immune, may fly). The mode is per-player
  (`GameMode`), toggled via the pause menu (`SetGameModeRequest`); flight is gated to Creative client-side.

Death is `Health ≤ 0`: the network layer latches `ClientSession.Dead`, broadcasts it in the stats packet, and
holds the player until a `RespawnRequest` (the death screen) resets stats + teleports to spawn. Stats persist
per player (see [networking.md](networking.md) / `PlayerSerializer`). Eating drives hunger back up — see
[inventory.md](inventory.md).

**Taking mob damage.** A hostile creature in melee range applies `PlayerSurvival.ApplyContactDamage`, the single
armor-reducible damage path: worn armor (`EntityPlayer.Inventory.ArmorDefense()`) cuts the hit by 4% per defense
point (capped 80%), then `Health` drops. It's survival-only and no-ops when dead. (Fall/drowning/void bypass
armor, matching Minecraft, so they stay direct subtractions.) Player knockback is omitted because the client owns
player physics. See [inventory.md](inventory.md) for armor items + slots.
## Rendering

`EntityRenderer` ([Client/Graphics/EntityRenderer.cs](../MinecraftClone3API/Client/Graphics/EntityRenderer.cs))
draws every entity into the deferred **G-buffer** with the `EntityGeometry` shader (real normals + a per-entity
flat light value sampled at its position, so it shades/shadows like the surrounding blocks — see
[rendering.md](rendering.md)). Box models are textured from the **official Minecraft entity sheets**: each box uses
Mojang's texture offsets + dimensions so the sheet maps straight on, with UVs normalized by the texture array's
layer size. (Current Minecraft splits some mob sheets by climate variant, so the paths carry a suffix —
`entity/pig/pig_temperate`, `entity/cow/cow_temperate`, `entity/chicken/chicken_temperate`.) Dropped items render as the spinning, bobbing 3D icon of their block (the same mesh
[ItemIconRenderer](../MinecraftClone3API/Client/Graphics/ItemIconRenderer.cs) uses for the inventory). A
projectile (the ender pearl) instead renders as a single **camera-facing billboard** quad of its item sprite —
the quad's local axes are mapped onto the camera's `Right`/`Up`/forward each frame so it always faces the viewer.

The **local player** is normally excluded from rendering (it isn't in `world.Entities`), but in a third-person
view (F5, see [state-gameloop.md](state-gameloop.md)) `EntityRenderer.Render` also draws it with the built-in
player model at `PlayerController.PlayerEntity`. Its walk cycle is advanced from its own physics in
`PlayerController` (via `Entity.AdvanceWalkCycle`), not the network-interpolation path the other entities use.

**Models are data, not code.** Each type's geometry is a **Bedrock-edition geometry JSON** file (the
Blockbench-native mob format — bones with `pivot`/`rotation`, cubes with absolute `origin`/`size`/`uv`), loaded
by `BedrockModelLoader` ([Client/Graphics/BedrockModelLoader.cs](../MinecraftClone3API/Client/Graphics/BedrockModelLoader.cs)).
Mob geometry is **not** in the Minecraft jar (it lives in compiled Java there), so — like `Vanilla/Models/Water.json`
— these are the few authored model files we ship: `System/Models/Entity/biped.geo.json` (the plain humanoid, used
by the zombie), `System/Models/Entity/player.geo.json` (the same biped **plus the modern skin's overlay layer** —
hat/jacket/sleeves/pants, each a base part copied and grown by cube `inflate`, textured from the lower sheet rows;
overlay bones reuse the base part name so they swing with the limb), and the animals under
`Vanilla/Models/Entity/`. The loader reflects Bedrock's −Z-facing, y-up
coordinates into our +Z-facing convention (`z → −z`) and rebases each cube's absolute origin onto its bone pivot;
it honors per-cube `uv`/`inflate` and X-axis bone rotation (the quadruped body pitch), but not bone parenting,
cube `mirror`, or Y/Z rotation conventions (unused by the built-in models). Bone names drive animation, so author
them `head`/`body`/`leg0..3`/`arm0..1`/`wing0..1`.

**Overlay layers.** An `EntityType` may name a second model + texture (`OverlayModelPath`/`OverlayTexturePath`) —
the sheep's wool. It's built as a separate `RenderModel` (its own texture indexes into the array, so each VAO's
vertices already reference the right layer — no extra bind) and drawn over the base with the **same** per-part
matrices, since the overlay reuses the base bone names/pivots. The renderer skips it when the entity's
`EntityData.OverlayVisible` is false (a sheared sheep). The player's hat/jacket layer is instead baked into
`player.geo.json` because it shares the player's one skin texture.

Some humanoid sheets (e.g. the zombie) use the **legacy single-layer layout**: the left arm/leg regions are
blank and the right-limb texels serve both sides. At model-build time `MirrorEmptyLimbs` detects a fully
transparent left-limb (`arm1`/`leg1`) region in the texture and copies the matching right-limb (`arm0`/`leg0`)
UVs onto it, so legacy sheets keep all four limbs while a modern skin (the player's, with painted left limbs)
is left untouched.

Per-type models are built **once** in `EntityRenderer.LoadModels`, which runs in the load flow **after** plugins
register their types and **before** `BlockTextureManager.Upload` — so the entity textures are indexed into the
arrays before they're baked to the GPU. Animation (limb swing keyed by part name `leg*`/`arm*`/`wing*`/`head`,
item spin) is matrix-only at draw time; the shared meshes stay static. Culling is disabled while drawing entities
(box models emit all six faces of each box); depth still resolves visibility.

## Adding an entity

1. Add a **Bedrock geometry** `*.geo.json` under your plugin's `Assets/<NS>/Models/Entity/` (author it in
   Blockbench, or hand-write it — bones with `pivot`/`rotation`, cubes with `origin`/`size`/`uv`). Name the bones
   `head`/`body`/`leg0..3`/`arm0..1`/`wing0..1` so the animator picks them up; a quadruped/bird body bone gets a
   `"rotation": [90, 0, 0]` pitch.
2. `context.Register(new EntityType(...))` in your plugin, pointing at the official texture path and the model's
   resource key (e.g. `Vanilla/Models/Entity/cow.geo.json`).
3. Creatures spawn ambiently; spawn explicitly with `world.SpawnEntity(type, pos)`, or register an
   `ItemSpawnEgg(type, spritePath)` for a right-click creative spawn egg.

No on-disk or wire format changes are involved (entities aren't saved), so no world reset is needed.
