using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using Silk.NET.Maths;
using Silk.NET.Input;

namespace MinecraftClone3.States
{
    /// <summary>
    /// Top-level options screen. An overlay over whatever opened it (the main menu state or the pause-menu
    /// overlay); its buttons open the per-category sub-screens (<see cref="GuiGraphicsOptions"/>,
    /// <see cref="GuiControls"/>) as further overlays, and Done/Escape reveals the opener again.
    /// </summary>
    internal class GuiOptions : GuiBase
    {
        private const int ButtonWidth = 200;
        private const int ButtonHeight = 40;
        private const int ButtonGap = 12;
        private const int TitleScale = 3;
        private const string Title = "Options";

        public GuiOptions()
        {
            ClientResources.Input.CursorMode = CursorMode.Normal;

            var x = ((int) ScaledResolution.GuiResolution.X - ButtonWidth) / 2;
            var y = (int) ScaledResolution.GuiResolution.Y / 2 - (3 * ButtonHeight + 2 * ButtonGap) / 2;
            var step = ButtonHeight + ButtonGap;

            Elements.Add(new GuiButton(Rectangle.FromSize(x, y, ButtonWidth, ButtonHeight), "Graphics...",
                () => StateEngine.AddOverlay(new GuiGraphicsOptions())));
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + step, ButtonWidth, ButtonHeight), "Controls...",
                () => StateEngine.AddOverlay(new GuiControls())));
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + 2 * step, ButtonWidth, ButtonHeight), "Done",
                () => IsDead = true));
        }

        public override void OnKeyDown(Key key)
        {
            if (key == Key.Escape)
                IsDead = true;
        }

        public override void Render()
        {
            var screen = new Vector2D<int>(ClientResources.Width, ClientResources.Height);
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, new Rectangle(0, 0, screen.X, screen.Y), null,
                new Vector4D<float>(0f, 0f, 0f, 0.7f), false);

            var width = (int) ScaledResolution.GuiResolution.X;
            var height = (int) ScaledResolution.GuiResolution.Y;
            var titleX = (width - Font.MeasureWidth(Title, TitleScale)) / 2;
            Font.DrawString(Title, titleX, height / 4, TitleScale, new Vector4D<float>(1f,1f,1f,1f));

            base.Render();
        }
    }
}
