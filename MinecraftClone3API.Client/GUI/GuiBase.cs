using System.Collections.Generic;
using MinecraftClone3API.Client.StateSystem;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace MinecraftClone3API.Client.GUI
{
    public abstract class GuiBase : StateBase
    {
        protected List<GuiElementBase> Elements = new List<GuiElementBase>();

        public override void Update(bool focused) => Elements.ForEach(e => e.Update(focused));

        public override void Render() => Elements.ForEach(e => e.Render());

        public override void OnMouseDown(MouseButton button, Vector2D<float> guiPos)
            => Elements.ForEach(e => e.OnMouseDown(button, guiPos));

        public override void OnMouseUp(MouseButton button, Vector2D<float> guiPos)
            => Elements.ForEach(e => e.OnMouseUp(button, guiPos));

        public override void OnKeyDown(Key key) => Elements.ForEach(e => e.OnKeyDown(key));

        public override void OnCharTyped(char c) => Elements.ForEach(e => e.OnCharTyped(c));

        public override void OnScroll(float delta) => Elements.ForEach(e => e.OnScroll(delta));
    }
}
