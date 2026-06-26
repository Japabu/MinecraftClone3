using System.Collections.Generic;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// GPU-free description of a boxy entity model in the Minecraft mob-model style: a flat list of
    /// <see cref="ModelPart"/>s (head, body, legs, …), each a group of texture-mapped boxes that pivots
    /// independently for animation. Coordinates are authored in <b>blocks, Y-up, with the feet at y=0 and the
    /// model facing +Z</b> (the renderer yaws it to face its heading). Box texture UVs use the classic
    /// box-unwrap into a single <see cref="TexWidth"/>×<see cref="TexHeight"/> sheet, so the official Minecraft
    /// entity textures map straight on. Held by <see cref="MinecraftClone3API.Entities.EntityType"/>; the
    /// renderer turns it into GPU buffers once per type.
    /// </summary>
    public class EntityModel
    {
        public readonly int TexWidth;
        public readonly int TexHeight;
        public readonly List<ModelPart> Parts = new List<ModelPart>();

        public EntityModel(int texWidth, int texHeight)
        {
            TexWidth = texWidth;
            TexHeight = texHeight;
        }

        public ModelPart AddPart(ModelPart part)
        {
            Parts.Add(part);
            return part;
        }
    }

    /// <summary>One animatable group of boxes, rotated about <see cref="Pivot"/> (the joint). The animation
    /// role is matched by <see cref="Name"/> (e.g. <c>head</c>, <c>leg0</c>..<c>leg3</c>, <c>arm0</c>/<c>arm1</c>,
    /// <c>wing0</c>/<c>wing1</c>) so the renderer knows how to swing it.</summary>
    public class ModelPart
    {
        public readonly string Name;
        public readonly Vector3 Pivot;

        /// <summary>Constant orientation applied on top of any walk-cycle animation (radians). Used to lay a
        /// quadruped's body box horizontal while its texture unwraps from the upright box dimensions.</summary>
        public Vector3 Rotation;

        public readonly List<ModelBox> Boxes = new List<ModelBox>();

        public ModelPart(string name, Vector3 pivot)
        {
            Name = name;
            Pivot = pivot;
        }

        public ModelPart(string name, Vector3 pivot, Vector3 rotation) : this(name, pivot)
        {
            Rotation = rotation;
        }

        /// <summary>Adds a box. <paramref name="from"/>/<paramref name="to"/> are in blocks, relative to this
        /// part's <see cref="Pivot"/>; <paramref name="texU"/>/<paramref name="texV"/> is the box's top-left
        /// corner in the texture sheet (texels). <paramref name="inflate"/> (blocks) grows the rendered geometry
        /// on every side while the UV unwrap stays at the base size — Minecraft's overlay-layer "delta".</summary>
        public ModelPart AddBox(Vector3 from, Vector3 to, int texU, int texV, float inflate = 0f)
        {
            Boxes.Add(new ModelBox(from, to, texU, texV, inflate));
            return this;
        }
    }

    public class ModelBox
    {
        public readonly Vector3 From;
        public readonly Vector3 To;

        /// <summary>Uniform expansion of the rendered box on all sides (blocks); the UV unwrap is derived from the
        /// un-inflated <see cref="From"/>/<see cref="To"/> size, so a grown overlay box still samples its base
        /// texture region.</summary>
        public readonly float Inflate;

        /// <summary>Top-left corner of the box's unwrap in the texture sheet (texels). Mutable so the renderer
        /// can remap a legacy humanoid's empty left-limb boxes onto the right-limb texels.</summary>
        public int TexU;
        public int TexV;

        public ModelBox(Vector3 from, Vector3 to, int texU, int texV, float inflate)
        {
            From = from;
            To = to;
            TexU = texU;
            TexV = texV;
            Inflate = inflate;
        }
    }
}
