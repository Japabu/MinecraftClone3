using System;
using System.Collections.Generic;
using System.Linq;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.IO;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Newtonsoft.Json;
using Silk.NET.Maths;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Minecraft's first-person held-item placement, sourced from the resource pack exactly like vanilla: a
    /// model's <c>display.&lt;context&gt;</c> transform (read from the pack JSON with its <c>parent</c> chain
    /// resolved) composed with <c>ItemInHandRenderer</c>'s hand constants and swing. Change a model's display
    /// block in a resource pack and the held pose changes with it. A held block reads its already-parsed
    /// <see cref="BlockModel.Display"/>; a flat item's model is read on demand — <em>transforms only</em>, so
    /// this never loads a texture, never touches the GPU, and never throws on an unresolved sprite.
    /// </summary>
    internal static class ItemDisplay
    {
        // Minecraft ItemInHandRenderer.ITEM_POS_{X,Y,Z}: the right-hand base position, applied outermost (the
        // item arm transform translate, in view space).
        private static readonly Matrix4 RightHand = Matrix4X4.CreateTranslation(0.56f, -0.52f, -0.72f);

        private const string FirstPerson = "firstperson_righthand";

        // Vanilla item/block firstperson_righthand, used when a model declares no display block of its own.
        private static readonly BlockModel.DisplayEntry FlatDefault = new BlockModel.DisplayEntry
            {Rotation = new Vector3(0f, -90f, 25f), Translation = new Vector3(1.13f, 3.2f, 1.13f), Scale = new Vector3(0.68f)};
        private static readonly BlockModel.DisplayEntry BlockDefault = new BlockModel.DisplayEntry
            {Rotation = new Vector3(0f, 45f, 0f), Translation = Vector3.Zero, Scale = new Vector3(0.4f)};

        private static readonly Dictionary<ushort, BlockModel.DisplayEntry> ItemCache =
            new Dictionary<ushort, BlockModel.DisplayEntry>();

        /// <summary>The first-person model matrix for the held stack, in view space (multiply by <c>view⁻¹</c>
        /// to pin it to the camera). <paramref name="swing"/> is <see cref="Swing"/> at the current progress.</summary>
        public static Matrix4 FirstPersonPose(Item item, Block block, Matrix4 swing)
            => DisplayMatrix(block != null ? BlockFirstPerson(block) : ItemFirstPerson(item)) * swing * RightHand;

        private static BlockModel.DisplayEntry BlockFirstPerson(Block block)
            => block.Model?.Display != null && block.Model.Display.TryGetValue(FirstPerson, out var d) ? d : BlockDefault;

        private static BlockModel.DisplayEntry ItemFirstPerson(Item item)
        {
            if (ItemCache.TryGetValue(item.Id, out var cached)) return cached;
            var display = item.TexturePath != null ? ReadDisplay(ModelPathFor(item.TexturePath)) : null;
            var entry = display != null && display.TryGetValue(FirstPerson, out var d) ? d : FlatDefault;
            return ItemCache[item.Id] = entry;
        }

        // "minecraft/textures/item/stick.png" -> "minecraft/models/item/stick"; a "ns:item/stick" location is
        // already the model id (its .json resolves under models/).
        private static string ModelPathFor(string texturePath) =>
            texturePath.Contains("/textures/")
                ? texturePath.Replace("/textures/", "/models/").Replace(".png", "")
                : texturePath;

        // Minecraft ItemTransform.apply: the model vertex is scaled, then rotated (euler XYZ — which acts on the
        // vertex as Z·Y·X), then translated (translation is in 1/16-block units). Row-vector order: scale first.
        private static Matrix4 DisplayMatrix(BlockModel.DisplayEntry d)
        {
            var scale = d.Scale == Vector3.Zero ? Vector3.One : d.Scale;
            return Matrix4X4.CreateScale(scale) *
                   Matrix4X4.CreateRotationZ(Scalar.DegreesToRadians(d.Rotation.Z)) *
                   Matrix4X4.CreateRotationY(Scalar.DegreesToRadians(d.Rotation.Y)) *
                   Matrix4X4.CreateRotationX(Scalar.DegreesToRadians(d.Rotation.X)) *
                   Matrix4X4.CreateTranslation(d.Translation * (1f / 16f));
        }

        /// <summary>Minecraft <c>ItemInHandRenderer.swingArm</c> + <c>applyItemArmAttackTransform</c> for the
        /// right hand, driven by the 0..1 swing progress. Identity at 0, so it doubles as the rest pose.</summary>
        public static Matrix4 Swing(float a)
        {
            if (a <= 0f) return Matrix4.Identity;
            var sqrt = MathF.Sqrt(a);
            var xz = MathF.Sin(sqrt * MathF.PI);
            var yy = MathF.Sin(a * a * MathF.PI);
            return Matrix4X4.CreateRotationY(Scalar.DegreesToRadians(-45f)) *
                   Matrix4X4.CreateRotationX(Scalar.DegreesToRadians(xz * -80f)) *
                   Matrix4X4.CreateRotationZ(Scalar.DegreesToRadians(xz * -20f)) *
                   Matrix4X4.CreateRotationY(Scalar.DegreesToRadians(45f + yy * -20f)) *
                   Matrix4X4.CreateTranslation(
                       -0.4f * MathF.Sin(sqrt * MathF.PI),
                       0.2f * MathF.Sin(sqrt * MathF.PI * 2f),
                       -0.2f * MathF.Sin(a * MathF.PI));
        }

        // Resolves a model's `display` block from the pack, following `parent`, reading only the transforms.
        private static Dictionary<string, BlockModel.DisplayEntry> ReadDisplay(string modelPath)
        {
            var resolved = BlockModel.GetRelativePaths(modelPath, modelPath, ".json").FirstOrDefault(ResourceReader.Exists);
            if (resolved == null) return null;

            var sources = new List<string>();
            var current = resolved;
            var source = ResourceReader.ReadString(current);
            while (true)
            {
                sources.Add(source);
                var parent = JsonConvert.DeserializeObject<ParentRef>(source)?.Parent;
                if (string.IsNullOrEmpty(parent) || sources.Count > 100) break;
                var parentFile = BlockModel.GetRelativePaths(current, parent, ".json").FirstOrDefault(ResourceReader.Exists);
                if (parentFile == null) break;
                current = parentFile;
                source = ResourceReader.ReadString(current);
            }

            sources.Reverse();
            var model = new DisplayModel();
            try { sources.ForEach(s => JsonConvert.PopulateObject(s, model)); }
            catch (Exception e) { Logger.Error($"Could not read display for \"{modelPath}\""); Logger.Exception(e); }
            return model.Display;
        }

        private class ParentRef
        {
            public string Parent { get; set; }
        }

        private class DisplayModel
        {
            public string Parent { get; set; }

            // A field pre-seeded with an empty map so PopulateObject merges each source's display keys into it
            // (parent first, child overrides), matching BlockModel's display-inheritance behaviour.
            public readonly Dictionary<string, BlockModel.DisplayEntry> Display = new Dictionary<string, BlockModel.DisplayEntry>();
        }
    }
}
