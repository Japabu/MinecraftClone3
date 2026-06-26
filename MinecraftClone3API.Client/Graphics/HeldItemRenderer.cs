using System;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Entities;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Draws the first-person held-item viewmodel in the lower-right as a true 3D model: a held block as its 3D
    /// block model (or box model for a block entity like a chest), and a flat item as its extruded sprite mesh
    /// (<see cref="HeldItemMeshes"/>), exactly like Minecraft. It renders into the G-buffer in the overlay pass
    /// (so the composition lights it), pinned to a fixed view-space pose via <c>uModel = pose · view⁻¹</c> and
    /// compressed into the front of the depth range so the world never clips it.
    /// <see cref="PlayerController.SwingPhase"/> drives a swing arc on attack/mine/place/use. Empty hand: nothing.
    /// </summary>
    public static class HeldItemRenderer
    {
        public static void Render(WorldClient world, Camera camera, Matrix4 projection)
        {
            if (PlayerController.Perspective != PlayerController.CameraPerspective.FirstPerson) return;
            if (PlayerController.PlayerEntity == null) return;

            var stack = world.Inventory.SelectedItem;
            if (stack.IsEmpty) return;
            var item = stack.Item;
            if (item == null) return;
            var block = item.GetBlock();

            EntityRenderer.BindShaderRaw(camera.View, projection, out var uModel, out var uLight, out _);
            EntityRenderer.SetLight(world,
                PlayerController.PlayerEntity.RenderPosition + PlayerController.PlayerEntity.EyeOffset, uLight);

            // Sit on top of the world without clearing the shared G-buffer depth (the composition reads it):
            // compress this draw into the front 0..0.1 of the depth range while its parts still self-occlude.
            RenderState.Set(new GlState {CullFace = false, DepthTest = true, DepthFunc = DepthFunction.Lequal});
            GL.DepthRange(0.0, 0.1);

            var inverseView = camera.View.Inverted();
            var swing = SwingMatrix();

            if (block != null && block.RendersAsBlockEntity && BlockEntityRenderer.GetModel(block.Id) is EntityRenderer.RenderModel beModel)
            {
                // Box model authored centred on x/z with feet at y=0: drop it to centre, then place it.
                var pose = Matrix4.CreateTranslation(0f, -0.45f, 0f) * BlockPose(swing);
                EntityRenderer.DrawStaticParts(beModel, pose * inverseView, uModel);
            }
            else if (block != null && EntityRenderer.GetItemMesh(block.Id) is VertexArrayObject blockMesh)
            {
                var pose = BlockPose(swing) * inverseView;
                GL.UniformMatrix4(uModel, false, ref pose);
                blockMesh.Draw();
            }
            else if (HeldItemMeshes.Get(item) is VertexArrayObject itemMesh)
            {
                var pose = ItemPose(swing) * inverseView;
                GL.UniformMatrix4(uModel, false, ref pose);
                itemMesh.Draw();
            }

            GL.DepthRange(0.0, 1.0);
            RenderState.Set(new GlState {CullFace = true, DepthTest = true, DepthFunc = DepthFunction.Lequal});
        }

        // The swing arc: dip down and rotate forward over the 0..1..0 of the swing. (Tunable.)
        private static Matrix4 SwingMatrix()
        {
            var s = MathF.Sin(PlayerController.SwingPhase * MathF.PI);
            return Matrix4.CreateRotationX(-1.3f * s) * Matrix4.CreateTranslation(-0.1f * s, -0.25f * s, 0.1f * s);
        }

        // View-space placement of a held block (centred cube [-0.5,0.5]) in the lower-right, angled to show its
        // front + side. View space: camera at origin looking −Z, +X right, +Y up. (Tunable.)
        private static Matrix4 BlockPose(Matrix4 swing) =>
            Matrix4.CreateScale(0.40f) *
            Matrix4.CreateRotationX(MathHelper.DegreesToRadians(20f)) *
            Matrix4.CreateRotationY(MathHelper.DegreesToRadians(-45f)) *
            swing *
            Matrix4.CreateTranslation(0.56f, -0.52f, -0.80f);

        // View-space placement of a held flat item (the extruded sprite, centred [-0.5,0.5], thin in z), held
        // diagonally like Minecraft. The sprite faces the camera dead-on (no Y angle) so its front and back faces
        // coincide on screen rather than splitting into a doubled image; the swing's X-tilt reveals its depth.
        // (Tunable.)
        private static Matrix4 ItemPose(Matrix4 swing) =>
            Matrix4.CreateScale(0.85f) *
            Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(20f)) *
            Matrix4.CreateRotationY(MathHelper.DegreesToRadians(-90f)) *
            swing *
            Matrix4.CreateTranslation(0.65f, -0.32f, -0.70f);
    }
}
