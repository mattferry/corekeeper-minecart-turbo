using PugMod;
using UnityEngine;

namespace MinecartTurbo
{
    /// <summary>
    /// Mod entry point. Adds three crafted minecart tiers (2x / 3x / 4x top speed, recipe scaled by the same
    /// multiplier) to the Railway Forge. Must be installed on the server AND every client (requiredOn = 3):
    /// minecart movement is client-predicted, so both sides must agree on the cart's maxSpeed.
    ///
    /// Two-step registration (Core Keeper 1.3.0+): EarlyInit creates the tiers' runtime data blocks (only allowed
    /// before the game loads its data), then a Harmony prefix on ECSManager.Init (see MinecartTurboPatches) clones
    /// the vanilla Minecart into them - the last moment before the game converts the data blocks into entities.
    /// Update() is a belt-and-braces fallback that does the same thing earlier if the prefix did not apply; both
    /// paths are idempotent.
    /// </summary>
    public class MinecartTurboMod : IMod
    {
        public const string Name = "Minecart Turbo";
        public const string ModName = "MinecartTurbo";   // ModManifest.json "name"
        public const string Version = "1.2.0";

        public void EarlyInit()
        {
            Debug.Log("[MinecartTurbo] " + Version + " loaded - Turbo Minecart (2x / 3x / 4x) at the Railway Forge.");
            MinecartTurboRegistry.CreateDataBlocks(this);
        }

        public void Init() { }
        public void Shutdown() { }
        public void ModObjectLoaded(Object obj) { }

        public void Update()
        {
            MinecartTurboRegistry.TryRegister("IMod.Update");
            MinecartTurboRegistry.SweepPhantoms();
        }
    }
}
