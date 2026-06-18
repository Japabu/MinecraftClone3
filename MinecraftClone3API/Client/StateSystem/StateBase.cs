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
    }
}
