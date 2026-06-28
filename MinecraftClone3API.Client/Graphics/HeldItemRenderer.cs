using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics.Rhi;
using Silk.NET.Maths;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Draws the first-person held-item viewmodel in the lower-right as a true 3D model: a held block as its 3D
    /// block model (or box model for a block entity like a chest), and a flat item as its extruded sprite mesh
    /// (<see cref="HeldItemMeshes"/>), exactly like Minecraft. It draws into the G-buffer in the overlay section
    /// of the geometry pass (so the composition lights it), pinned to a fixed view-space pose via
    /// <c>model = pose · view⁻¹</c> and compressed into the front of the (reverse-Z) depth range so the world
    /// never clips it. <see cref="PlayerController.SwingPhase"/> drives a swing arc on attack/mine/place/use.
    /// Empty hand: nothing.
    /// </summary>
    public static class HeldItemRenderer
    {
        private static readonly EntityRenderer.EntityDrawList _list = new EntityRenderer.EntityDrawList("heldItem");

        public static void Render(RenderPass pass, WorldClient world, Camera camera)
        {
            if (PlayerController.Perspective != PlayerController.CameraPerspective.FirstPerson) return;
            if (PlayerController.PlayerEntity == null) return;

            var stack = world.Inventory.SelectedItem;
            if (stack.IsEmpty) return;
            var item = stack.Item;
            if (item == null) return;
            var block = item.GetBlock();

            Matrix4X4.Invert(camera.View, out var inverseView);
            var swing = ItemDisplay.Swing(PlayerController.SwingPhase);
            var eye = PlayerController.PlayerEntity.RenderPosition + PlayerController.PlayerEntity.EyeOffset;
            var light = EntityRenderer.SampleLight(world, eye);

            _list.Clear();

            if (block != null && block.RendersAsBlockEntity &&
                BlockEntityRenderer.GetModel(block.Id) is EntityRenderer.RenderModel beModel)
            {
                // Box model authored centred on x/z with feet at y=0: centre it in Y (height 14/16) before the
                // block display transform scales/rotates it about its centre, exactly like a normal held block.
                var pose = Matrix4X4.CreateTranslation(0f, -0.4375f, 0f) *
                           ItemDisplay.FirstPersonPose(item, block, swing) * inverseView;
                EntityRenderer.EnqueueStaticParts(_list, beModel, pose, light);
            }
            else if (block != null && block.ItemSpriteTexture != null &&
                     HeldItemMeshes.GetByTexture(block.ItemSpriteTexture) is EntityRenderer.PartMesh spriteMesh)
            {
                // A flat-icon block (torch/ladder/flower) is held as its extruded sprite with the flat item pose.
                var pose = ItemDisplay.FirstPersonPose(item, null, swing) * inverseView;
                _list.Enqueue(spriteMesh, pose, light);
            }
            else if (block != null && EntityRenderer.GetItemMesh(block.Id) is EntityRenderer.PartMesh blockMesh)
            {
                var pose = ItemDisplay.FirstPersonPose(item, block, swing) * inverseView;
                _list.Enqueue(blockMesh, pose, light);
            }
            else if (HeldItemMeshes.Get(item) is EntityRenderer.PartMesh itemMesh)
            {
                var pose = ItemDisplay.FirstPersonPose(item, null, swing) * inverseView;
                _list.Enqueue(itemMesh, pose, light);
            }

            if (_list.Count == 0) return;

            // WebGPU has no glDepthRange; under reverse-Z (near=1, far=0, compare Greater) the viewmodel must
            // occupy the depth band nearest 1 so terrain never clips it, while its own parts self-occlude within
            // the band. SetViewport's min/max depth is the equivalent; restore the full range after.
            float w = ClientResources.Width;
            float h = ClientResources.Height;
            pass.SetViewport(0, 0, w, h, 0.9f, 1.0f);
            _list.Flush(pass);
            pass.SetViewport(0, 0, w, h, 0f, 1f);
        }
    }
}
