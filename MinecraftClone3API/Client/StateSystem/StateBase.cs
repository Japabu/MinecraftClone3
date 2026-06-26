namespace MinecraftClone3API.Client.StateSystem
{
    public abstract class StateBase
    {
        public bool IsDead = false;

        /// <param name="focused">
        /// Whether this layer is the foreground and may read input. Backgrounded layers still
        /// tick every frame, they just ignore input.
        /// </param>
        public abstract void Update(bool focused);
        public abstract void Render();
        public virtual void Exit() { }

        /// <summary>Whether this overlay should freeze the underlying singleplayer world while it is open.
        /// Only the pause menu does; container/inventory screens leave the world running (mobs keep moving,
        /// furnaces keep smelting), exactly as vanilla. Read by <see cref="StateEngine.WorldPaused"/>.</summary>
        public virtual bool PausesWorld => false;
    }
}
