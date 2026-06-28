using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using Silk.NET.Maths;
using Silk.NET.Input;

namespace MinecraftClone3.States
{
    internal class GuiMainMenu : GuiBase
    {
        private const int ButtonWidth = 200;
        private const int ButtonHeight = 40;
        private const int ButtonGap = 12;
        private const int TitleScale = 4;
        private const string Title = "MinecraftClone3";

        private static Texture _background;

        public GuiMainMenu()
        {
            ClientResources.Input.CursorMode = CursorMode.Normal;

            if (_background == null)
                _background = GlResources.ReadTexture("System/Textures/Gui/ResourceLoadingBackground.png");

            var x = ((int) ScaledResolution.GuiResolution.X - ButtonWidth) / 2;
            var y = (int) ScaledResolution.GuiResolution.Y / 2 - (4 * ButtonHeight + 3 * ButtonGap) / 2;
            var step = ButtonHeight + ButtonGap;

            Elements.Add(new GuiButton(Rectangle.FromSize(x, y, ButtonWidth, ButtonHeight), "Singleplayer",
                () => StateEngine.ReplaceState(new GuiWorldSelection())));
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + step, ButtonWidth, ButtonHeight), "Multiplayer",
                () => StateEngine.ReplaceState(new StateWorld(multiplayer: true))));
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + 2 * step, ButtonWidth, ButtonHeight), "Options",
                () => StateEngine.AddOverlay(new GuiOptions())));
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + 3 * step, ButtonWidth, ButtonHeight), "Quit Game",
                () => ClientResources.Window.Close()));
        }

        public override void Update(bool focused) => base.Update(focused);

        public override void Render()
        {
            GuiRenderer.DrawCover(_background);

            var width = (int) ScaledResolution.GuiResolution.X;
            var height = (int) ScaledResolution.GuiResolution.Y;
            var titleX = (width - Font.MeasureWidth(Title, TitleScale)) / 2;
            Font.DrawString(Title, titleX, height / 6, TitleScale, new Vector4D<float>(1f,1f,1f,1f));

            base.Render();
        }
    }
}
