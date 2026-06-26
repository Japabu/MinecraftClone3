using System.Collections.Generic;
using MinecraftClone3API.Client;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Draws the progressive block-breaking crack overlay (Minecraft's <c>destroy_stage_0..9</c>) over the block
    /// the player is mining. The textured cube is blended into the G-buffer diffuse only (the normal + light
    /// attachments are masked, so the block keeps its own shading) and composition then lights the darkened
    /// surface, so the cracks read like part of the block. Draws nothing when no resource pack supplies the
    /// stage textures.
    /// </summary>
    public static class BlockBreakRenderer
    {
        private const int Stages = 10;

        private static VertexArrayObject _vbo;
        private static Texture[] _stages;
        private static bool _stagesLoaded;

        public static void Load()
        {
            _vbo = new VertexArrayObject();
            BuildCube();
            _vbo.Upload();
        }

        public static void Render(AxisAlignedBoundingBox boundingBox, Vector3 translation, float progress,
            Camera camera, Matrix4 projection)
        {
            if (_vbo == null || progress <= 0f) return;

            EnsureStages();
            var stage = (int) (progress * Stages);
            if (stage < 0) stage = 0;
            else if (stage >= Stages) stage = Stages - 1;
            var texture = _stages[stage];
            if (texture == null) return;

            // A touch larger than the block so the crack sits just proud of the surface and never z-fights it.
            var transform = Matrix4.CreateScale(boundingBox.Scale * 1.002f) *
                            Matrix4.CreateTranslation(boundingBox.Translation + translation) *
                            camera.View * projection;

            RenderState.Set(new GlState
            {
                Blend = true,
                BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha),
                DepthTest = true,
                DepthFunc = DepthFunction.Lequal,
                CullFace = false
            });

            // Touch the diffuse attachment only and don't write depth — the block's geometry-pass normal/light
            // and depth must survive so it shades normally and the far cube faces stay depth-culled.
            GL.DepthMask(false);
            GL.ColorMask(1u, false, false, false, false);
            GL.ColorMask(2u, false, false, false, false);

            var shader = ClientResources.BlockBreakShader;
            shader.Bind();
            GL.UniformMatrix4(shader.GetUniformLocation("uTransform"), false, ref transform);
            GL.Uniform1(shader.GetUniformLocation("uTexture"), 0);
            texture.Bind(TextureUnit.Texture0);
            Samplers.BindGuiSampler(0);
            _vbo.Draw();

            GL.ColorMask(1u, true, true, true, true);
            GL.ColorMask(2u, true, true, true, true);
            GL.DepthMask(true);
        }

        private static void EnsureStages()
        {
            if (_stagesLoaded) return;
            _stagesLoaded = true;

            _stages = new Texture[Stages];
            for (var i = 0; i < Stages; i++)
            {
                var path = "minecraft/textures/block/destroy_stage_" + i + ".png";
                if (ResourceReader.Exists(path)) _stages[i] = GlResources.ReadTexture(path);
            }
        }

        private static void BuildCube()
        {
            var indices = new List<uint>();

            AddFace(indices, new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(0.5f, 0.5f, -0.5f));       // +X
            AddFace(indices, new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, 0.5f));     // -X
            AddFace(indices, new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f));       // +Y
            AddFace(indices, new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, -0.5f));   // -Y
            AddFace(indices, new Vector3(0.5f, -0.5f, 0.5f), new Vector3(-0.5f, -0.5f, 0.5f),
                new Vector3(-0.5f, 0.5f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f));       // +Z
            AddFace(indices, new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f));     // -Z

            _vbo.AddFace(indices.ToArray(), Vector3.Zero);
        }

        private static void AddFace(List<uint> indices, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            var baseIndex = (uint) _vbo.VertexCount;
            _vbo.Add(a, new Vector4(0, 0, 0, 0), Vector4.Zero, Vector3.Zero, Vector4.Zero);
            _vbo.Add(b, new Vector4(1, 0, 0, 0), Vector4.Zero, Vector3.Zero, Vector4.Zero);
            _vbo.Add(c, new Vector4(1, 1, 0, 0), Vector4.Zero, Vector3.Zero, Vector4.Zero);
            _vbo.Add(d, new Vector4(0, 1, 0, 0), Vector4.Zero, Vector3.Zero, Vector4.Zero);

            indices.Add(baseIndex + 0);
            indices.Add(baseIndex + 1);
            indices.Add(baseIndex + 2);
            indices.Add(baseIndex + 0);
            indices.Add(baseIndex + 2);
            indices.Add(baseIndex + 3);
        }
    }
}
