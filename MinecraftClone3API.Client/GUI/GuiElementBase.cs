using Silk.NET.Input;
using Silk.NET.Maths;

namespace MinecraftClone3API.Client.GUI
{
    public abstract class GuiElementBase
    {
        /// <summary>Per-frame tick for continuous state (hover from the live cursor position); not for input edges.</summary>
        public abstract void Update(bool focused);
        public abstract void Render();

        // Discrete input, routed from the owning GuiBase (which receives it from StateEngine when it is the
        // foreground layer). guiPos is the cursor in 960x540 GUI space. Default no-op.
        public virtual void OnMouseDown(MouseButton button, Vector2D<float> guiPos) { }
        public virtual void OnMouseUp(MouseButton button, Vector2D<float> guiPos) { }
        public virtual void OnKeyDown(Key key) { }
        public virtual void OnCharTyped(char c) { }
        public virtual void OnScroll(float delta) { }
    }
}
