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
            var swing = ItemDisplay.Swing(PlayerController.SwingPhase);

            if (block != null && block.RendersAsBlockEntity && BlockEntityRenderer.GetModel(block.Id) is EntityRenderer.RenderModel beModel)
            {
                // Box model authored centred on x/z with feet at y=0: centre it in Y (height 14/16) before the
                // block display transform scales/rotates it about its centre, exactly like a normal held block.
                var pose = Matrix4.CreateTranslation(0f, -0.4375f, 0f) *
                           ItemDisplay.FirstPersonPose(item, block, swing) * inverseView;
                EntityRenderer.DrawStaticParts(beModel, pose, uModel);
            }
            else if (block != null && EntityRenderer.GetItemMesh(block.Id) is VertexArrayObject blockMesh)
            {
                var pose = ItemDisplay.FirstPersonPose(item, block, swing) * inverseView;
                GL.UniformMatrix4(uModel, false, ref pose);
                blockMesh.Draw();
            }
            else if (HeldItemMeshes.Get(item) is VertexArrayObject itemMesh)
            {
                var pose = ItemDisplay.FirstPersonPose(item, null, swing) * inverseView;
                GL.UniformMatrix4(uModel, false, ref pose);
                itemMesh.Draw();
            }

            GL.DepthRange(0.0, 1.0);
            RenderState.Set(new GlState {CullFace = true, DepthTest = true, DepthFunc = DepthFunction.Lequal});
        }
    }
}
