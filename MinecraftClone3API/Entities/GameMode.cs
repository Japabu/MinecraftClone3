namespace MinecraftClone3API.Entities
{
    /// <summary>A player's game mode. Survival applies health/hunger/damage and forbids flight; Creative is
    /// immune and may free-fly. Server-authoritative — toggled via the pause menu and synced in the player
    /// stats packet.</summary>
    public enum GameMode : byte
    {
        Survival = 0,
        Creative = 1
    }
}
