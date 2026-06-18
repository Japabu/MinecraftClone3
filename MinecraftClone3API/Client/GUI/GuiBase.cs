using System.Collections.Generic;
using MinecraftClone3API.Client.StateSystem;

namespace MinecraftClone3API.Client.GUI
{
    public abstract class GuiBase : StateBase
    {
        protected List<GuiElementBase> Elements = new List<GuiElementBase>();

        public override void Update(bool focused) => Elements.ForEach(e => e.Update(focused));

        public override void Render() => Elements.ForEach(e => e.Render());
    }
}
