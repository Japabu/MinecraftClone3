# Resource & plugin loading

`GuiResourceLoading` (client) and `MinecraftClone3Server/Program.LoadPlugins` (server) mirror each other:
`CommonResources.Load()` → add the `System` plugin → add every dir/zip in `Plugins/` →
**`PluginManager.AddResourcePacks()`** (cascade user packs) → `PluginManager.LoadResources` → `LoadPlugins`.
**The server stops there; the client additionally does the GL-only steps** (`ClientResources.Load`,
`BoundingBoxRenderer.Load`, `EntityRenderer.Load`, `BlockTextureManager.Upload`). Plugin model JSON and PNGs
are read CPU-side via StbImage, so they load fine headless; only the texture-array *upload* is GL.

**Animated textures (frame strips).** A texture whose height is a whole multiple of its width is a Minecraft
animation sheet (e.g. `water_still` = 16×512, 32 frames; also lava/fire). `ResourceReader.ReadBlockTexture`
detects this, and `BlockTextureManager.LoadAnimatedTexture` slices it into square per-frame layers and
uploads **all** of them plus the `.mcmeta` `frametime` into `BlockTextureManager.AnimatedTextures`. Block
faces sample **frame 0** only, but every frame is retained so a future animator can cycle them with no
re-slice. Without this a strip would land in the square texture array mismapped.

**Water.** Vanilla ships no cube model for water (its `model.json` is empty — vanilla uses a bespoke fluid
renderer), and `water_still.png` is a grey **tint-mask**. So `VanillaPlugin` authors a minimal cube model
(`Vanilla/Models/Water.json`, parent `System/Models/Block`) referencing the real `minecraft:block/water_still`
texture with `tintindex 0`, and `BlockWater` returns the vanilla default water blue from `GetTintColor` (the
mesher drops tint *alpha*, so translucency comes from the texture's own alpha, ~0.7). The block is decoupled
from its *look*: the animated surface + Fresnel sky reflection + sun specular (**Tier B**) live entirely in
the composition shader, flagged by `BlockWater.GetRenderMaterial`. A refractive forward water pass (Tier C)
is deferred.

Because server-side light simulation calls `Block.GetLightLevel`, **block code that runs on the server must
not touch client/GL/window state** (this is what crashed `BlockTorch` — it read the keyboard).

Content staging (see the two exe `.csproj` files): the `System` plugin (from `MinecraftClone3/Content/System`)
and `VanillaPlugin` (its content + freshly built DLL under `Dlls/`) are copied next to each executable so both
resolve `Plugins/` against `AppContext.BaseDirectory`.

**The resource layer is a generic path→bytes cascade** (`ResourceManager`). `AddFileSystem` indexes every
file under a source's `Assets/` prefix (case-insensitive) keyed by the path *after* the prefix, storing the
`(FileSystem, original full path)` so reads go back through the source's *own* casing — a real Minecraft jar
holds lowercase `assets/...` and `ZipArchive.GetEntry` is case-sensitive on every OS, so `LoadAsset` reads by
the stored path, not by reconstructing `"Assets/" + key`. Files under a `data/` prefix are indexed the same way
into a parallel `DataIndices` (read via `LoadData`/`ExistsData`/`DataKeys`), so a pack's data tree — crafting
recipes and item tags — is available too. Nothing is parsed at index time except `Lang/*.lang` (eagerly
merged); models/textures/recipes are parsed **on demand** by the consumers, so only the handful of referenced
assets ever get decoded. **Load order = cascade priority:** System → plugins → resource packs (within packs by
name); a later-loaded source containing a key wins.

**Translations come from the pack too.** Besides plugin `Lang/*.lang` files, `I18N.Load` scans the indexed
assets for any `…/lang/<code>.json` (the Minecraft flat `key`→`value` format) and merges them. Language codes
are normalized (lower-case, `-`→`_`) so a plugin's `en-US` and a pack's `en_us.json` share one bucket. Blocks
and items resolve their names through Minecraft translation keys (`block.minecraft.stone`,
`item.minecraft.stick`) built from their `MinecraftId`, so a client jar supplies every name with no
hand-written lang file. **Recipes** are likewise pack-driven: `RecipeLoader` reads `data/<ns>/recipe/*.json`
and `data/<ns>/tags/item/*.json` after plugins load (see [inventory.md](inventory.md)).

**Resource packs** live in `GamePaths.ResourcePacksDir` (`~/.local/share/MinecraftClone3/ResourcePacks/`,
created on access with a `README.txt`). `PluginManager.AddResourcePacks()` scans it **after** the plugins —
subdirs → `FileSystemRaw`, `*.zip`/`*.jar` → `FileSystemCompressed`, sorted by name, each in a try/catch —
and `AddResourcePack` indexes each (assets + lang only; **no `PluginInfo.json`/DLL**, so a plain client jar
loads without the "no info file" error).

**The Vanilla plugin ships almost no models or textures** (those are Mojang-derived) — only code, its `Lang/`,
and the handful of assets the jar genuinely lacks: the `Water.json` cube model and the Bedrock **entity geometry**
files under `Vanilla/Models/Entity/` (mob geometry is compiled Java in the jar, not data — see
[entities.md](entities.md)). It references blocks by **explicit Minecraft resource locations** (`minecraft:block/stone`,
`minecraft:block/grass_block`, …) and the engine loads the real Minecraft model JSON + PNGs from a
user-provided pack. The vanilla model format **is** the engine's format (`parent`/`textures`
`#vars`/`elements`/`faces`); extra fields (`shade`, `gui_light`, element `rotation`) are silently dropped by
`JsonConvert.PopulateObject`. The only gap is **reference syntax**: Minecraft uses namespaced resource
locations `[namespace:]path` (default `minecraft:`; category implied by context).
`BlockModel.GetRelativePaths` resolves these by **appending** a candidate `{ns}/{category}/{loc}{extension}`
(`.json`→`models`, `.png`→`textures`) to its relative candidates, run by both `ReadBlockModel` and
`ReadBlockTexture` — so `minecraft:block/stone` finds `minecraft/models/block/stone.json`. The candidate is
purely additive, so the System plugin's relative refs and the no-pack fallback are unchanged. The plugin pins
to **1.13+ vanilla naming** (singular `block/`, `grass_block_*`, `*_stained_glass`) — a different jar version
may need ref updates. `minecraft-assets/` (gitignored, extracted from a client jar) doubles as a ready dev
pack.

**No pack present:** models/textures fail to resolve → `MissingModel`/`MissingTexture` fallback; the game
runs with placeholder blocks, no crash. **The GUI font is also pack-sourced** — `Font` (`Client/GUI/Font.cs`)
loads `minecraft/font/default.json` + its `minecraft:font/*.png` bitmaps from the pack, so with no pack
`Font.Load` logs an error and text rendering is disabled (the rest still runs). **The sky's sun/moon textures
are likewise pack-sourced** — `WorldRenderer.LoadSkyTextures()` (right after `Font.Load`) reads
`minecraft/textures/environment/{sun,moon_phases}.png`; with no pack the skybox draws procedural discs.
