# Minecart Turbo — faster minecarts for Core Keeper

Three craftable minecarts at the Railway Forge. Each tier multiplies the vanilla Minecart's top
speed — **and its crafting cost** — by the same number. That's the whole design: pay 3x, go 3x.

**Get it on mod.io: https://mod.io/g/corekeeper/m/minecartturbo**

| Item | Top speed | Recipe (Railway Forge) | Name colour |
|---|---|---|---|
| Turbo Minecart (2x) | 1600 (~28 tiles/s) | 20 Iron Bar + 12 Tin Bar | Rare (blue) |
| Turbo Minecart (3x) | 2400 (~42 tiles/s) | 30 Iron Bar + 18 Tin Bar | Epic (purple) |
| Turbo Minecart (4x) | 3200 (~56 tiles/s) | 40 Iron Bar + 24 Tin Bar | Legendary (orange) |

(Vanilla Minecart: 800, 10 Iron Bar + 6 Tin Bar.) Acceleration and braking are unchanged game
constants, so a faster cart takes longer to reach top speed and longer to stop — brake early on
the 4x. Works on Core Keeper 1.2.1.5. No CoreLib, no dependencies, no Unity project — the game
compiles these sources itself at boot (official PugMod system).

## Multiplayer

Minecart movement is client-predicted, so this is `requiredOn: 3` — the mod must be installed on
the **server and every client** (the join screen offers the subscription automatically).
Dedicated servers: add `minecartturbo` to the mods list.

## How it works

At boot (a Harmony prefix on `ECSManager.Init`, with an `IMod.Update` fallback) the mod clones
the game's own `MinecartEntity` authoring prefab three times, converts each clone to the
string-named `ObjectAuthoring` form, scales `MinecartAuthoring.maxSpeed`, the recipe and the
sell value, and registers the clones through `API.Authoring.RegisterAuthoringGameObject`. An
`OnObjectTypeAdded` hook then appends the three recipes to the Railway Forge's
`CanCraftObjectsBuffer` (deduped, so it's idempotent per world).

## Notes for other Core Keeper modders — two engine traps this mod hit so you don't have to

1. **Runtime-cloned authoring objects must be tagged as ECS `Prefab`s.** The game's
   `ConversionManager` only adds `Unity.Entities.Prefab` when the source GameObject is an asset
   outside any scene; a clone you `Instantiate` at runtime is a scene object, so every world
   gets a *live* entity instead of a prefab. Live entities never register in the NetCode ghost
   collection, but client-side ghost conversion still marks them as predicted spawns — and
   `PredictedGhostSpawnSystem` then throws *"Could not find ghost type in the collection"*
   inside a Burst job, killing the client instantly on any world load, with no error dialog.
   Fix: in your `OnObjectTypeAdded` handler (which runs before ghost conversion), add `Prefab`
   to the entity and its `LinkedEntityGroup` members, with the entity itself at index 0. See
   `MakeProperPrefab` in `Scripts/MinecartTurboRegistry.cs`.

2. **I2 localization rewrites `:` to `_` on lookup but not on import.** Item name terms are
   `"Items/" + objectName`, and `LocalizationManager.TryGetTranslation` does
   `Term.Replace(':', '_')` before searching — while `Import_CSV` stores your keys verbatim.
   If your object names contain `:` (the conventional `ModName:ObjectName` form), your CSV keys
   must use the underscore form (`Items/MinecartTurbo_Mk1`) or every name renders as a raw key.

Also: **identity is permanent.** ObjectIDs are assigned at boot (sequential from 32768 in
name-hash order for this mod) and the NetCode ghost type is SHA1-hashed from the clone
GameObject name — renaming an object orphans every placed/stored item of that type. If you're
adding your own entities alongside other mods, prefer pinning explicit IDs by pre-seeding
`Manager.mod.Authoring.ObjectIDLookup` before conversion (see the companion
[Turbo Boats](https://github.com/mattferry/corekeeper-turbo-boats) mod for that pattern).

## Developing

There's nothing to build — edit the `.cs` files and the game compiles them at launch. To
type-check locally without launching, run Roslyn's net472 `csc.exe` (from the
`Microsoft.Net.Compilers.Toolset` NuGet package) referencing every DLL in the game's
`CoreKeeper_Data/Managed/`. `publish/modio_publish.py` uploads a release to mod.io (reads
`MODIO_TOKEN` and `MODIO_API_URL` from the environment; zip layout = manifest at the zip root).

## Credits

Written by Claude (Anthropic), piloted by Matt Ferry, for the Ferry family server — published
in case someone else's rail network is also too slow. MIT licensed; see [LICENSE](LICENSE).
