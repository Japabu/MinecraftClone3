using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MinecraftClone3.States
{
    /// <summary>
    /// Lists the saved singleplayer worlds and lets the player create, load, or delete one. A state (not an
    /// overlay) so the create/delete navigations replace it cleanly; deletion is confirmed via a GuiConfirm
    /// overlay, which is safe here since this screen owns no TextInput subscriptions.
    /// </summary>
    internal class GuiWorldSelection : GuiBase
    {
        private const int ButtonHeight = 40;
        private const int ButtonGap = 14;
        private const int TitleScale = 3;
        private const string Title = "Select World";

        private static Texture _background;

        private readonly GameWindow _window;
        private readonly GuiWorldList _list;
        private readonly GuiButton _play;
        private readonly GuiButton _delete;

        public GuiWorldSelection(GameWindow window)
        {
            _window = window;
            _window.CursorState = CursorState.Normal;

            if (_background == null)
                _background = GlResources.ReadTexture("System/Textures/Gui/ResourceLoadingBackground.png");

            var width = (int) ScaledResolution.GuiResolution.X;
            var height = (int) ScaledResolution.GuiResolution.Y;

            const int margin = 80;
            var listBounds = new Rectangle(margin, 70, width - margin, height - 80);
            _list = new GuiWorldList(listBounds, WorldManager.ListWorlds(), Play);
            Elements.Add(_list);

            const int buttonWidth = 180;
            var totalWidth = 4 * buttonWidth + 3 * ButtonGap;
            var x = (width - totalWidth) / 2;
            var y = height - ButtonHeight - 24;
            var step = buttonWidth + ButtonGap;

            _play = new GuiButton(Rectangle.FromSize(x, y, buttonWidth, ButtonHeight), "Play Selected World",
                () => { if (_list.Selected != null) Play(_list.Selected); });
            Elements.Add(_play);

            Elements.Add(new GuiButton(Rectangle.FromSize(x + step, y, buttonWidth, ButtonHeight), "Create New World",
                () => StateEngine.ReplaceState(new GuiCreateWorld(_window))));

            _delete = new GuiButton(Rectangle.FromSize(x + 2 * step, y, buttonWidth, ButtonHeight), "Delete", Delete);
            Elements.Add(_delete);

            Elements.Add(new GuiButton(Rectangle.FromSize(x + 3 * step, y, buttonWidth, ButtonHeight), "Back",
                () => StateEngine.ReplaceState(new GuiMainMenu(_window))));
        }

        private void Play(WorldInfo world)
        {
            WorldManager.MarkPlayed(world);
            StateEngine.ReplaceState(new StateWorld(_window, world));
        }

        private void Delete()
        {
            var sel = _list.Selected;
            if (sel == null) return;
            StateEngine.AddOverlay(new GuiConfirm(_window, $"Delete \"{sel.Name}\"?", () =>
            {
                WorldManager.DeleteWorld(sel);
                StateEngine.ReplaceState(new GuiWorldSelection(_window));
            }));
        }

        public override void Update(bool focused)
        {
            base.Update(focused);

            var hasSelection = _list.Selected != null;
            _play.Enabled = hasSelection;
            _delete.Enabled = hasSelection;

            if (focused && _window.KeyboardState.IsKeyPressed(Keys.Escape))
                StateEngine.ReplaceState(new GuiMainMenu(_window));
        }

        public override void Render()
        {
            RenderState.Set(new GlState
            {
                Blend = true,
                BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
            });

            var width = (int) ScaledResolution.GuiResolution.X;
            var height = (int) ScaledResolution.GuiResolution.Y;
            GuiRenderer.DrawTexture(_background, new Rectangle(0, 0, width, height), null);

            var titleX = (width - Font.MeasureWidth(Title, TitleScale)) / 2;
            Font.DrawString(Title, titleX, 18, TitleScale, Color4.White);

            base.Render();
        }
    }
}
