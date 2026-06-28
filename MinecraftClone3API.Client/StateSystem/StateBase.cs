using Silk.NET.Input;
using Silk.NET.Maths;

namespace MinecraftClone3API.Client.StateSystem
{
    public abstract class StateBase
    {
        public bool IsDead = false;

        /// <param name="focused">
        /// Whether this layer is the foreground and may read held input. Backgrounded layers still
        /// tick every frame, they just ignore input.
        /// </param>
        public abstract void Update(bool focused);
        public abstract void Render();
        public virtual void Exit() { }

        /// <summary>Whether this overlay should freeze the underlying singleplayer world while it is open.
        /// Only the pause menu does; container/inventory screens leave the world running (mobs keep moving,
        /// furnaces keep smelting), exactly as vanilla. Read by <see cref="StateEngine.WorldPaused"/>.</summary>
        public virtual bool PausesWorld => false;

        // Discrete input events, delivered by StateEngine only to the foreground layer (event-driven Silk
        // input; held/continuous state is read directly from ClientResources.Input). guiPos is the cursor in
        // 960x540 GUI space. Defaults no-op so a state overrides only what it uses.
        public virtual void OnMouseDown(MouseButton button, Vector2D<float> guiPos) { }
        public virtual void OnMouseUp(MouseButton button, Vector2D<float> guiPos) { }
        public virtual void OnKeyDown(Key key) { }
        public virtual void OnCharTyped(char c) { }
        public virtual void OnScroll(float delta) { }
    }
}
