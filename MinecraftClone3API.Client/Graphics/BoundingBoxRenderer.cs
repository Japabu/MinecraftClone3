using MinecraftClone3API.Graphics.Rhi;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Graphics
{
    /// <summary>Draws a black wireframe box around an axis-aligned bounding box (the block-selection outline),
    /// using the shared <see cref="OutlineRenderer"/> cube transformed onto the box.</summary>
    public static class BoundingBoxRenderer
    {
        private static readonly Vector4 OutlineColor = new Vector4(0f, 0f, 0f, 1f);

        public static void Load() => OutlineRenderer.Load();

        public static void Render(RenderPass pass, AxisAlignedBoundingBox boundingBox, Vector3 translation, float scale)
        {
            var mvp = Matrix4X4.CreateScale(boundingBox.Scale * scale) *
                      Matrix4X4.CreateTranslation(boundingBox.Translation + translation) *
                      Renderer.View * Renderer.Projection;
            OutlineRenderer.Draw(pass, mvp, OutlineColor);
        }
    }
}
