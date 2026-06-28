using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using Silk.NET.Maths;
using Silk.NET.Input;

namespace MinecraftClone3.States
{
    internal class GuiPauseMenu : GuiBase
    {
        private const int ButtonWidth = 200;
        private const int ButtonHeight = 40;
        private const int ButtonGap = 12;
        private const int TitleScale = 3;
        private const string Title = "Game Menu";

        private readonly WorldClient _world;
        private readonly GuiButton _gameModeButton;

        public GuiPauseMenu(WorldClient world)
        {
            _world = world;
            ClientResources.Input.CursorMode = CursorMode.Normal;

            var x = ((int) ScaledResolution.GuiResolution.X - ButtonWidth) / 2;
            var y = (int) ScaledResolution.GuiResolution.Y / 2 - (4 * ButtonHeight + 3 * ButtonGap) / 2;
            var step = ButtonHeight + ButtonGap;

            Elements.Add(new GuiButton(Rectangle.FromSize(x, y, ButtonWidth, ButtonHeight), "Back to Game", Close));
            _gameModeButton = new GuiButton(Rectangle.FromSize(x, y + step, ButtonWidth, ButtonHeight),
                GameModeLabel(), ToggleGameMode);
            Elements.Add(_gameModeButton);
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + 2 * step, ButtonWidth, ButtonHeight), "Options",
                () => StateEngine.AddOverlay(new GuiOptions())));
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + 3 * step, ButtonWidth, ButtonHeight),
                "Save and Quit to Title", () => StateEngine.ReplaceState(new GuiSavingWorld())));
        }

        public override bool PausesWorld => true;

        private string GameModeLabel() => "Game Mode: " + _world.GameMode;

        private void ToggleGameMode()
        {
            var target = _world.GameMode == GameMode.Survival ? GameMode.Creative : GameMode.Survival;
            _world.SendSetGameMode(target);
        }

        public override void Update(bool focused)
        {
            base.Update(focused);
            // The mode is server-authoritative; refresh the label once the change round-trips.
            _gameModeButton.Label = GameModeLabel();
        }

        public override void OnKeyDown(Key key)
        {
            if (key == Key.Escape)
                Close();
        }

        public override void Render()
        {
            var screen = new Vector2D<int>(ClientResources.Width, ClientResources.Height);
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, new Rectangle(0, 0, screen.X, screen.Y), null,
                new Vector4D<float>(0f, 0f, 0f, 0.5f), false);

            var width = (int) ScaledResolution.GuiResolution.X;
            var height = (int) ScaledResolution.GuiResolution.Y;
            var titleX = (width - Font.MeasureWidth(Title, TitleScale)) / 2;
            Font.DrawString(Title, titleX, height / 4, TitleScale, new Vector4D<float>(1f,1f,1f,1f));

            base.Render();
        }

        private void Close()
        {
            ClientResources.Input.CursorMode = CursorMode.Raw;
            PlayerController.ResetMouse();
            IsDead = true;
        }
    }
}
