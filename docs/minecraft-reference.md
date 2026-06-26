# Finding out how Minecraft does things

The project rule is **match Minecraft exactly and never hardcode/ship assets** — so whenever a model,
transform, UV layout, recipe, or mechanic is in question, *read it out of the user's own jar* rather than
guessing or copying an external/Bedrock source. There are two tiers of source-of-truth, both inside one jar.

## The jar

The resource pack the game already uses **is** a (deobfuscated, Mojang-mapped) client jar — class names are
clean (`ChestModel`, `LivingEntityRenderer`), not obfuscated (`a`, `b`):

```
~/Library/Application Support/MinecraftClone3/ResourcePacks/minecraft-<ver>-client.jar
```

(`~/.local/share/MinecraftClone3/...` on Linux.) `unzip -l "$JAR" | less` to browse it.

## Tier 1 — data assets (prefer this; no decompiling)

Most content is data-driven JSON/PNG under `assets/minecraft/`. These are authoritative — read or extract
them directly, don't reverse-engineer behavior that's already declared as data:

| Want | Path in jar |
|---|---|
| Block models / item models | `assets/minecraft/models/block/*.json`, `.../models/item/*.json` |
| Blockstate → model mapping | `assets/minecraft/blockstates/*.json` |
| Textures | `assets/minecraft/textures/**/*.png` |
| Recipes | `data/minecraft/recipe/*.json` (a `data/` tree, not `assets/`) |
| Names / i18n | `assets/minecraft/lang/en_us.json` |

```
unzip -p "$JAR" assets/minecraft/models/block/oak_stairs.json
```

## Tier 2 — hardcoded behavior (decompile with cfr)

Some things have **no data model** — they're compiled Java: block-entity geometry (chest/bed/sign/banner/
shulker/conduit), per-renderer transforms, the cube UV unwrap, entity animation math, combat/physics
constants. `models/block/chest.json` is just `{textures:{particle:...}}`; the real boxes live in
`net/minecraft/client/model/object/chest/ChestModel.class`.

Decompile to **real Java** (not `javap` bytecode) with `cfr-decompiler` (`brew install cfr-decompiler`):

```bash
# extract one class (or a whole package subtree) then decompile it
unzip -o "$JAR" 'net/minecraft/client/model/object/chest/ChestModel.class' -d /tmp/cls
cfr-decompiler /tmp/cls/net/minecraft/client/model/object/chest/ChestModel.class
# find a class by name first:
unzip -l "$JAR" | grep -i chestrenderer
```

### Classes worth knowing

- **`net/minecraft/client/model/geom/ModelPart$Cube`** — the *one* box→UV unwrap shared by every boxy model:
  down (−Y) face → first top-row region `[depth, depth+width]`, up (+Y) → second, V inverted.
- **`...renderer/entity/LivingEntityRenderer`** vs the block-entity renderers (**`...renderer/blockentity/
  ChestRenderer`** etc.) — the render-time `PoseStack` transforms. Mobs get a `scale(-1,-1,1)` **Y-flip**;
  block entities (chest) get **none** (pure yaw). That per-renderer difference — not any per-cube flag — is
  why mob skins and the chest texture use opposite top/bottom regions (see [rendering.md](rendering.md)).
- **`*Model` classes** (`ChestModel`, `BedModel`, …) — `texOffs(u,v) addBox(x,y,z, w,h,d)`
  `PartPose.offset(px,py,pz)` give boxes/UVs/pivots; `setupAnim` gives the animation (chest lid
  `xRot = -(1-(1-openness)³)·90°`).

### Porting Tier-2 geometry into our engine

Author a Bedrock `geo.json` for `BedrockModelLoader`: **absolute coords, feet at y=0, centred on x/z** (the
renderer's `+0.5` cell translate re-centres it), and remember the loader **mirrors z** (`z→−z`). Account for
the Y-flip per [rendering.md](rendering.md) (`flipUv` is a property of the build path, never a per-cube flag).
