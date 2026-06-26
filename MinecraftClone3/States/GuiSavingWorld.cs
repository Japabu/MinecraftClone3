using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;

namespace MinecraftClone3.States
{
    /// <summary>
    /// Shown after "Save and Quit to Title" while the world saves on its background teardown thread
    /// (see <see cref="StateWorld.Exit"/>). Because that save runs off the render thread, this screen keeps
    /// drawing every frame — the window never goes unresponsive — and switches to the main menu the moment the
    /// save finishes. A multiplayer quit (nothing to save locally) passes straight through to the title.
    /// </summary>
    internal class GuiSavingWorld : GuiBase
    {
        private const string Message = "Saving world...";
        private const int MessageScale = 3;

        private static Texture _background;

        private readonly GameWindow _window;

        public GuiSavingWorld(GameWindow window)
        {
            _window = window;
            if (_background == null)
                _background = GlResources.ReadTexture("System/Textures/Gui/ResourceLoadingBackground.png");
        }

        public override void Update(bool focused)
        {
            // The teardown thread was started in StateWorld.Exit before this screen became active; once it has
            // finished saving, reveal the title. Transitions immediately when there is nothing to save.
            if (!StateWorld.IsTearingDown)
                StateEngine.ReplaceState(new GuiMainMenu(_window));
        }

        public override void Render()
        {
            GuiRenderer.DrawTexture(_background, new Rectangle(0, 0, 960, 540), null);

            var width = (int) ScaledResolution.GuiResolution.X;
            var height = (int) ScaledResolution.GuiResolution.Y;
            var x = (width - Font.MeasureWidth(Message, MessageScale)) / 2;
            Font.DrawString(Message, x, height / 2, MessageScale, Color4.White);
        }
    }
}
