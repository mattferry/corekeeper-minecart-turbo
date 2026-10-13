using System;
using System.Collections.Generic;
using PugMod;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace MinecartTurbo
{
    /// <summary>
    /// Clones the vanilla Minecart authoring prefab three times at boot, turns each clone into a new object
    /// ("MinecartTurbo:Mk1/2/3" = 2x/3x/4x) with maxSpeed and crafting cost scaled by its multiplier, hands each
    /// clone to the game as an EntityAuthoringDataBlock so it is converted and databased like any vanilla object,
    /// and appends the three recipes to the Railway Forge's recipe list when the forge is converted.
    ///
    /// CORE KEEPER 1.3.0 (the 1.2.0 port): the game no longer keeps its object prefabs in PugDatabaseAuthoring.prefabList.
    /// Every object is now an EntityAuthoringDataBlock (a ScriptableDataBlock loaded through Addressables), and BOTH the
    /// conversion pass (PugDatabaseConverter) and the object database blob (PugDatabasePostConverter - the thing every
    /// recipe / inventory / placement lookup reads) are built ONLY from ScriptableData.GetDataBlocks&lt;EntityAuthoringDataBlock&gt;().
    /// The mod API's RegisterAuthoringGameObject list (ExtraAuthoring) is no longer converted at all. So a mod object must
    /// be a runtime EntityAuthoringDataBlock, and those can only be created in IMod.EarlyInit (before data loading starts)
    /// - which is before the vanilla Minecart prefab exists to clone from. Hence the two-step dance below: EarlyInit
    /// creates empty blocks (fixed addresses), and the ECSManager.Init prefix attaches the clones to them.
    ///
    /// PINNED ObjectIDs: 1.0.0/1.1.0 got sequential IDs in name-hash order (Mk3=32768, Mk2=32769, Mk1=32770 in every live
    /// and test world). Data blocks convert in address order, so from 1.2.0 the tiers PIN those exact IDs by pre-seeding
    /// Manager.mod.Authoring.ObjectIDLookup (ObjectAuthoring.TryGetPreferredObjectIndex honours the seed). A Harmony
    /// postfix on ECSManager.CalculateReservedObjectIndices then starts the sequential counter at 32771, so no other mod's
    /// unpinned object can ever land on a cart ID.
    ///
    /// LOCALIZATION TRAP: I2's LocalizationManager.TryGetTranslation rewrites ':' to '_' in every looked-up term,
    /// but the CSV import keeps ':' as-is. Item name terms are "Items/" + objectName, so the CSV keys MUST use the
    /// underscore form (Items/MinecartTurbo_Mk1), never the object name's colon form.
    ///
    /// IDENTITY IS PERMANENT: the ObjectIDs above and the NetCode ghost type (hashed from the clone's GameObject name)
    /// identify every cart already placed or stored in a world. Renaming a tier or re-pinning orphans them.
    /// </summary>
    public static class MinecartTurboRegistry
    {
        private sealed class Tier
        {
            public string ObjectName;   // registered object name -> localization key "Items/<name with : as _>"
            public int PinnedID;        // the ObjectID this tier has held in live worlds since 1.0.0
            public string BlockAddress; // fixed address of the tier's runtime EntityAuthoringDataBlock (stable across boots)
            public float Multiplier;    // applied to maxSpeed, every recipe ingredient and the sell value
            public Rarity Rarity;       // purely cosmetic: name colour in the UI marks the tier
            public EntityAuthoringDataBlock Block;   // created in EarlyInit, prefab attached at registration
            public string CloneName { get { return ObjectName.Replace(':', '_'); } }   // ghost type derives from this
        }

        private static readonly Tier[] Tiers =
        {
            new Tier { ObjectName = "MinecartTurbo:Mk1", PinnedID = 32770, BlockAddress = "b6bb13cb-8db9-4e22-8d82-65bef406bacd", Multiplier = 2.0f, Rarity = Rarity.Rare },
            new Tier { ObjectName = "MinecartTurbo:Mk2", PinnedID = 32769, BlockAddress = "6165bf73-2291-4e98-9b9b-35c70f4b09bb", Multiplier = 3.0f, Rarity = Rarity.Epic },
            new Tier { ObjectName = "MinecartTurbo:Mk3", PinnedID = 32768, BlockAddress = "2c503258-9062-4cef-aab7-6a83d2f78ad3", Multiplier = 4.0f, Rarity = Rarity.Legendary },
        };

        /// <summary>The ObjectID range this mod owns. Sequential mod IDs are pushed above it (see MinecartTurboPatches).</summary>
        public const int FirstReservedID = 32768;
        public const int LastReservedID = 32770;

        private const string Tag = "[MinecartTurbo] ";

        private static bool _blocksCreated;
        private static bool _attempted;
        private static bool _subscribed;
        private static readonly List<GameObject> _clones = new List<GameObject>();

        /// <summary>
        /// Step 1, from IMod.EarlyInit: create one empty runtime EntityAuthoringDataBlock per tier. The mod loader only
        /// allows this before ScriptableData loading starts, so it cannot wait for the vanilla prefab. Idempotent.
        /// </summary>
        public static void CreateDataBlocks(IMod owner)
        {
            if (_blocksCreated) return;
            _blocksCreated = true;
            try
            {
                if (API.DataBlocks == null)
                {
                    Debug.LogError(Tag + "API.DataBlocks is unavailable - cannot create runtime data blocks. Turbo minecarts NOT added.");
                    return;
                }
                long modId;
                if (!TryGetModId(owner, out modId))
                {
                    Debug.LogError(Tag + "could not find this mod's id in API.ModLoader.LoadedMods. Turbo minecarts NOT added.");
                    return;
                }
                int created = 0;
                foreach (Tier tier in Tiers)
                {
                    EntityAuthoringDataBlock block = API.DataBlocks.CreateRuntimeInstance<EntityAuthoringDataBlock>(modId, new DataBlockAddress(tier.BlockAddress));
                    block.name = tier.CloneName;   // named so the game's own "expected to point to a valid prefab" error would identify us
                    tier.Block = block;
                    created++;
                }
                Debug.Log(Tag + "created " + created + " runtime EntityAuthoringDataBlock(s) for mod id " + modId + " (prefabs attach at ECSManager.Init)");
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "runtime data block creation failed - turbo minecarts NOT added: " + e);
            }
        }

        private static bool TryGetModId(IMod owner, out long modId)
        {
            modId = 0;
            if (API.ModLoader == null) return false;
            foreach (LoadedMod mod in API.ModLoader.LoadedMods)
            {
                if (mod != null && mod.Handlers != null && mod.Handlers.Contains(owner)) { modId = mod.ModId; return true; }
            }
            foreach (LoadedMod mod in API.ModLoader.LoadedMods)
            {
                if (mod != null && mod.Metadata.name == MinecartTurboMod.ModName) { modId = mod.ModId; return true; }
            }
            return false;
        }

        /// <summary>
        /// Step 2. Idempotent; safe to call every frame. Does real work exactly once: after the data blocks are loaded
        /// (so the vanilla Minecart exists) and before ECSManager.Init's body runs (so our blocks still get converted).
        /// </summary>
        public static void TryRegister(string source)
        {
            if (_attempted) return;
            if (!ScriptableData.isLoaded) return;   // vanilla data blocks not in memory yet - try again later
            if (Manager.main == null) return;
            ECSManager ecs = Manager.ecs;
            if (ecs == null || API.Authoring == null) return;
            if (ecs.BlobAssetStore.IsCreated)
            {
                // Init's body has already started/finished: attaching prefabs now would be too late for conversion.
                // Refuse loudly instead of half-working.
                _attempted = true;
                Debug.LogError(Tag + "too late to register (" + source + "): ECSManager.Init already ran. Turbo minecarts NOT added.");
                return;
            }
            _attempted = true;
            try
            {
                Register(source);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "registration failed: " + e);
            }
        }

        private static void Register(string source)
        {
            foreach (Tier tier in Tiers)
            {
                if (tier.Block == null)
                {
                    Debug.LogError(Tag + "runtime data block for " + tier.ObjectName + " was not created in EarlyInit - turbo minecarts NOT added.");
                    return;
                }
            }
            MonoBehaviour vanilla = FindVanillaMinecart();
            if (vanilla == null)
            {
                Debug.LogError(Tag + "vanilla Minecart authoring prefab not found among the EntityAuthoringDataBlocks - turbo minecarts NOT added.");
                return;
            }
            if (!SeedPinnedObjectIDs())
            {
                Debug.LogError(Tag + "pinned ObjectID seeding failed - turbo minecarts NOT added.");
                return;
            }
            Subscribe();
            foreach (Tier tier in Tiers)
            {
                GameObject clone = CloneCart(vanilla.gameObject, tier);
                if (clone == null) continue;
                tier.Block.prefab = clone;                          // what the 1.3.0 converter and database actually read
                API.Authoring.RegisterAuthoringGameObject(clone);   // keeps the "Mod objects" category + PugDatabase registry in sync
                _clones.Add(clone);
            }
            Debug.Log(Tag + "registered " + _clones.Count + " turbo minecart prefab(s) cloned from '" + vanilla.name + "' (" + source + ")");
        }

        /// <summary>
        /// Pre-seeds the name->ObjectID lookup so each tier converts at its pinned ID (the one it has always had)
        /// instead of consuming a sequential index in data-block address order.
        /// </summary>
        private static bool SeedPinnedObjectIDs()
        {
            Dictionary<string, ObjectID> lookup = Manager.mod.Authoring.ObjectIDLookup;
            if (lookup == null)
            {
                Debug.LogError(Tag + "ObjectIDLookup unavailable - cannot pin IDs.");
                return false;
            }
            foreach (Tier tier in Tiers)
            {
                ObjectID existing;
                if (lookup.TryGetValue(tier.ObjectName, out existing))
                {
                    if ((int)existing != tier.PinnedID)
                    {
                        Debug.LogError(Tag + tier.ObjectName + " already mapped to ObjectID " + (int)existing
                                       + " (expected pin " + tier.PinnedID + ") - refusing to fight over it.");
                        return false;
                    }
                    continue;
                }
                lookup.Add(tier.ObjectName, (ObjectID)tier.PinnedID);
                Debug.Log(Tag + "pinned " + tier.ObjectName + " -> ObjectID " + tier.PinnedID);
            }
            return true;
        }

        /// <summary>
        /// Vanilla ingredient names must resolve BEFORE ECSManager.Init snapshots our ObjectInfo into PugDatabase
        /// (the crafting handler reads that snapshot, and it is never refreshed). The converters add the same
        /// name->ID pairs later, so seeding them early is harmless.
        /// </summary>
        private static void SeedVanillaName(string name, ObjectID id)
        {
            Dictionary<string, ObjectID> lookup = Manager.mod.Authoring.ObjectIDLookup;
            if (lookup == null) return;
            ObjectID existing;
            if (lookup.TryGetValue(name, out existing))
            {
                if (existing != id) Debug.LogError(Tag + "ingredient '" + name + "' is already mapped to " + (int)existing + ", expected " + (int)id);
                return;
            }
            lookup.Add(name, id);
        }

        private static MonoBehaviour FindVanillaMinecart()
        {
            IReadOnlyList<EntityAuthoringDataBlock> blocks = ScriptableData.GetDataBlocks<EntityAuthoringDataBlock>();
            if (blocks == null) return null;
            foreach (EntityAuthoringDataBlock block in blocks)
            {
                if (block == null || block.prefab == null) continue;
                IEntityMonoBehaviourData data = block.prefab.GetComponent(typeof(IEntityMonoBehaviourData)) as IEntityMonoBehaviourData;
                if (data == null) continue;
                ObjectInfo info = data.ObjectInfo;
                if (info == null || info.objectID != ObjectID.Minecart || info.variation != 0) continue;
                return data as MonoBehaviour;
            }
            return null;
        }

        private static GameObject CloneCart(GameObject source, Tier tier)
        {
            GameObject clone = UnityEngine.Object.Instantiate(source);
            clone.name = tier.CloneName;
            clone.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(clone);

            ObjectAuthoring oa = clone.GetComponent<ObjectAuthoring>();
            EntityMonoBehaviourData embd = clone.GetComponent<EntityMonoBehaviourData>();
            if (embd != null)
            {
                // Vanilla objects carry their data in an EntityMonoBehaviourData.objectInfo block with a fixed enum
                // ObjectID. A new object needs the string-named ObjectAuthoring form, so transplant the data.
                oa = ConvertToObjectAuthoring(clone, embd);
                UnityEngine.Object.DestroyImmediate(embd);
            }
            if (oa == null)
            {
                Debug.LogError(Tag + "'" + source.name + "' has neither ObjectAuthoring nor EntityMonoBehaviourData - cannot clone.");
                UnityEngine.Object.DestroyImmediate(clone);
                return null;
            }

            MinecartAuthoring cart = clone.GetComponent<MinecartAuthoring>();
            if (cart == null)
            {
                Debug.LogError(Tag + "'" + source.name + "' has no MinecartAuthoring - cannot clone.");
                UnityEngine.Object.DestroyImmediate(clone);
                return null;
            }

            oa.objectName = tier.ObjectName;
            oa.variation = 0;
            oa.rarity = tier.Rarity;

            LocalizationAuthoring loc = GetOrAdd<LocalizationAuthoring>(clone);
            loc.termKey = tier.ObjectName;

            InventoryItemAuthoring item = GetOrAdd<InventoryItemAuthoring>(clone);
            List<InventoryItemAuthoring.CraftingObject> recipe = new List<InventoryItemAuthoring.CraftingObject>();
            string recipeText = "";
            if (item.requiredObjectsToCraft != null)
            {
                foreach (InventoryItemAuthoring.CraftingObject ingredient in item.requiredObjectsToCraft)
                {
                    int amount = Mathf.CeilToInt(ingredient.amount * tier.Multiplier);
                    recipe.Add(new InventoryItemAuthoring.CraftingObject { objectName = ingredient.objectName, amount = amount });
                    recipeText += (recipeText.Length > 0 ? ", " : "") + amount + "x " + ingredient.objectName;
                }
            }
            item.requiredObjectsToCraft = recipe;
            if (item.sellValue > 0) item.sellValue = Mathf.RoundToInt(item.sellValue * tier.Multiplier);

            if (recipe.Count == 0)
            {
                // A free cart would be worse than no cart. Refuse this tier; the forge hook only adds recipes for
                // clones that registered.
                Debug.LogError(Tag + tier.ObjectName + ": source prefab has NO crafting recipe to scale - tier NOT added.");
                UnityEngine.Object.DestroyImmediate(clone);
                return null;
            }

            float baseSpeed = cart.maxSpeed;
            cart.maxSpeed = Mathf.Round(baseSpeed * tier.Multiplier);
            cart.currentSpeed = 0f;

            Debug.Log(Tag + tier.ObjectName + ": maxSpeed " + baseSpeed + " -> " + cart.maxSpeed + ", recipe " + recipeText
                      + ", rarity " + tier.Rarity + ", clone '" + clone.name + "', pin " + tier.PinnedID);
            return clone;
        }

        private static ObjectAuthoring ConvertToObjectAuthoring(GameObject clone, EntityMonoBehaviourData embd)
        {
            ObjectInfo info = embd.objectInfo;
            ObjectAuthoring oa = clone.AddComponent<ObjectAuthoring>();
            oa.initialAmount = info.initialAmount;
            oa.variation = info.variation;
            oa.variationIsDynamic = info.variationIsDynamic;
            oa.variationToToggleTo = info.variationToToggleTo;
            oa.objectType = info.objectType;
            oa.tags = info.tags != null ? new List<ObjectCategoryTag>(info.tags) : new List<ObjectCategoryTag>();
            oa.rarity = info.rarity;
            oa.salvageMultiplier = info.salvageMultiplier;
            oa.isCustomScenePrefab = info.isCustomScenePrefab;
            oa.additionalSprites = info.additionalSprites;
            PrefabInfo prefabInfo = info.prefabInfo;
            if (prefabInfo != null)
            {
                // 1.3.0: visuals are a GraphicalObjectDataBlock reference (pooled by address); the GameObject is the
                // fallback the converter uses when the reference is empty. Carry both = same visuals as the vanilla cart.
                oa.graphicalRef = prefabInfo.graphicalRef;
                oa.graphicalPrefab = prefabInfo.GetGraphical();
            }
            else
            {
                Debug.LogWarning(Tag + "vanilla minecart ObjectInfo has no prefabInfo - clone will have no visuals.");
            }
            if (info.iconSkinAssetRef.hasAddress)
            {
                Debug.LogWarning(Tag + "vanilla minecart has an icon skin reference that ObjectAuthoring cannot carry - the inventory icon may differ.");
            }

            InventoryItemAuthoring item = GetOrAdd<InventoryItemAuthoring>(clone);
            item.sellValue = info.sellValue;
            item.buyValueMultiplier = info.buyValueMultiplier;
            item.icon = info.icon;
            item.iconOffset = info.iconOffset;
            item.smallIcon = info.smallIcon;
            item.isStackable = info.isStackable;
            item.craftingTime = info.craftingTime;
            item.requiredObjectsToCraft = new List<InventoryItemAuthoring.CraftingObject>();
            if (info.requiredObjectsToCraft != null)
            {
                foreach (CraftingObject ingredient in info.requiredObjectsToCraft)
                {
                    string ingredientName = ingredient.objectID.ToString();
                    SeedVanillaName(ingredientName, ingredient.objectID);
                    item.requiredObjectsToCraft.Add(new InventoryItemAuthoring.CraftingObject
                    {
                        objectName = ingredientName,
                        amount = ingredient.amount
                    });
                }
            }

            LocalizationAuthoring loc = GetOrAdd<LocalizationAuthoring>(clone);
            loc.languageGenders = info.languageGenders;

            if (clone.GetComponent<PlaceableObjectAuthoring>() == null)
            {
                // Not expected (the placement rules of a placeable object live in this component), but if the vanilla
                // prefab keeps them elsewhere, reproduce the minecart's known rules: 1x1, placeable only on rails.
                Debug.LogWarning(Tag + "vanilla minecart has no PlaceableObjectAuthoring - adding one (1x1, rails only).");
                PlaceableObjectAuthoring placeable = clone.AddComponent<PlaceableObjectAuthoring>();
                placeable.prefabTileSize = info.prefabTileSize;
                placeable.prefabCornerOffset = info.prefabCornerOffset;
                placeable.centerIsAtEntityPosition = info.centerIsAtEntityPosition;
                placeable.appearInMapUI = info.appearInMapUI;
                placeable.mapColor = info.mapColor;
                placeable.canBePlacedOnAnyWalkableTile = false;
                placeable.canBePlacedOnObjects = new List<ObjectID> { ObjectID.Rail };
                placeable.canNotBePlacedOnObjects = new List<ObjectID>();
            }
            return oa;
        }

        private static T GetOrAdd<T>(GameObject go) where T : Component
        {
            T component = go.GetComponent<T>();
            return component != null ? component : go.AddComponent<T>();
        }

        private static void Subscribe()
        {
            if (_subscribed) return;
            API.Authoring.OnObjectTypeAdded += OnObjectTypeAdded;
            _subscribed = true;
        }

        /// <summary>Fires once per converted object type per world (after conversion). Hooks the Railway Forge.</summary>
        private static void OnObjectTypeAdded(Entity entity, GameObject authoring, EntityManager entityManager)
        {
            try
            {
                if (!entityManager.HasComponent<ObjectDataCD>(entity)) return;
                ObjectID id = entityManager.GetComponentData<ObjectDataCD>(entity).objectID;
                bool ours = false;
                foreach (Tier tier in Tiers)
                {
                    if (authoring.name == tier.CloneName)
                    {
                        ours = true;
                        MakeProperPrefab(entity, entityManager, tier.ObjectName);
                        if ((int)id == tier.PinnedID)
                        {
                            Debug.Log(Tag + tier.ObjectName + " converted at pinned ObjectID " + (int)id + " in world '" + entityManager.World.Name + "'");
                        }
                        else
                        {
                            Debug.LogError(Tag + "PINNED ID MISMATCH: " + tier.ObjectName + " converted as ObjectID " + (int)id
                                           + ", expected " + tier.PinnedID + " (world '" + entityManager.World.Name
                                           + "') - existing carts of this tier would be orphaned. DO NOT DEPLOY.");
                        }
                        LogRecipeResolution(tier, authoring);
                    }
                }
                if (!ours && (int)id >= FirstReservedID && (int)id <= LastReservedID)
                {
                    Debug.LogError(Tag + "FOREIGN OBJECT AT CART ID: '" + authoring.name + "' converted as ObjectID " + (int)id
                                   + " (world '" + entityManager.World.Name + "') - collides with the MinecartTurbo ID range ("
                                   + FirstReservedID + "-" + LastReservedID + ").");
                }
                if (id == ObjectID.RailwayForge) AddRecipes(entity, entityManager);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "OnObjectTypeAdded failed: " + e);
            }
        }

        /// <summary>
        /// Every ingredient name must resolve to a real ObjectID or the item crafts for free with an empty requirement
        /// list. Log the resolved content so the boot log proves the recipes are real.
        /// </summary>
        private static void LogRecipeResolution(Tier tier, GameObject authoring)
        {
            InventoryItemAuthoring item = authoring.GetComponent<InventoryItemAuthoring>();
            if (item == null || item.requiredObjectsToCraft == null || item.requiredObjectsToCraft.Count == 0)
            {
                Debug.LogError(Tag + tier.ObjectName + ": no recipe on converted authoring - would craft for FREE. DO NOT DEPLOY.");
                return;
            }
            string resolved = "";
            bool broken = false;
            foreach (InventoryItemAuthoring.CraftingObject ingredient in item.requiredObjectsToCraft)
            {
                ObjectID ingredientId = API.Authoring.GetObjectID(ingredient.objectName);
                if (ingredientId == ObjectID.None) broken = true;
                resolved += (resolved.Length > 0 ? ", " : "") + ingredient.amount + "x " + ingredient.objectName + "=" + (int)ingredientId;
            }
            if (broken)
            {
                Debug.LogError(Tag + tier.ObjectName + " recipe has UNRESOLVED ingredient(s): " + resolved + " - DO NOT DEPLOY.");
            }
            else
            {
                Debug.Log(Tag + tier.ObjectName + " recipe resolved: " + resolved);
            }
        }

        /// <summary>
        /// Root cause of the v1.0.0 client crash: the game's ConversionManager only tags a converted entity with
        /// Unity.Entities.Prefab when the source GameObject is an asset outside any scene (CreateAndEnqueue checks
        /// !gameObject.scene.IsValid()). Our clones are runtime scene objects, so every world got three LIVE cart
        /// entities instead of prefabs. Live entities never register in the NetCode ghost collection
        /// (GhostCollectionSystem only registers Prefab+GhostType entities), while on clients the ghost conversion
        /// adds PredictedGhostSpawnRequest to them - so PredictedGhostSpawnSystem's InitGhostJob saw live
        /// "predicted spawns" with unknown ghost types and threw "Could not find ghost type in the collection"
        /// inside a Burst job, killing the game the moment a client world started ticking (any join/world load).
        /// This handler (ModPostConverter) runs BEFORE GhostPostConverter, so tagging here makes ghost conversion
        /// and collection registration treat the carts exactly like vanilla prefab assets. Still true in 1.3.0.
        /// </summary>
        private static void MakeProperPrefab(Entity entity, EntityManager entityManager, string objectName)
        {
            bool wasLive = !entityManager.HasComponent<Prefab>(entity);
            Entity[] linked = null;
            if (entityManager.HasBuffer<LinkedEntityGroup>(entity))
            {
                DynamicBuffer<LinkedEntityGroup> buffer = entityManager.GetBuffer<LinkedEntityGroup>(entity);
                linked = new Entity[buffer.Length];
                for (int i = 0; i < buffer.Length; i++) linked[i] = buffer[i].Value;
            }
            if (wasLive) entityManager.AddComponent<Prefab>(entity);
            if (linked == null)
            {
                // No additional entities were attached by converters in this world: prefab groups always contain
                // themselves at index 0 (mirrors the ConversionManager asset branch).
                entityManager.AddBuffer<LinkedEntityGroup>(entity).Add(entity);
            }
            else
            {
                foreach (Entity other in linked)
                {
                    if (other != entity && !entityManager.HasComponent<Prefab>(other))
                    {
                        entityManager.AddComponent<Prefab>(other);
                    }
                }
                if (linked.Length == 0 || linked[0] != entity)
                {
                    DynamicBuffer<LinkedEntityGroup> buffer = entityManager.GetBuffer<LinkedEntityGroup>(entity);
                    buffer.Clear();
                    buffer.Add(entity);
                    foreach (Entity other in linked)
                    {
                        if (other != entity) buffer.Add(other);
                    }
                }
            }
            Debug.Log(Tag + objectName + (wasLive ? ": tagged as Prefab (was live - v1.0.0 bug)" : ": already a Prefab")
                      + ", linked entities " + ((linked != null) ? linked.Length : 1) + ", world '" + entityManager.World.Name + "'");
        }

        private static float _nextPhantomSweep;

        /// <summary>
        /// v1.0.0's untagged carts existed as live entities at the world origin in every server boot; if the world
        /// save picked them up they load back as real carts stacked at exactly (0,0,0) inside the Core. No
        /// legitimately placed cart can sit at the exact float origin, so any match is a leftover - delete it.
        /// Runs on the server world only, every 30 s, from IMod.Update (works even while the server is paused).
        /// </summary>
        public static void SweepPhantoms()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextPhantomSweep) return;
            _nextPhantomSweep = now + 30f;
            if (Manager.main == null) return;
            ECSManager ecs = Manager.ecs;
            World server = (ecs != null) ? ecs.ServerWorld : null;
            if (server == null || !server.IsCreated || API.Authoring == null) return;

            ObjectID id0 = API.Authoring.GetObjectID(Tiers[0].ObjectName);
            ObjectID id1 = API.Authoring.GetObjectID(Tiers[1].ObjectName);
            ObjectID id2 = API.Authoring.GetObjectID(Tiers[2].ObjectName);
            if (id0 == ObjectID.None) return;

            EntityManager em = server.EntityManager;
            EntityQuery query = em.CreateEntityQuery(
                ComponentType.ReadOnly<MinecartCD>(),
                ComponentType.ReadOnly<ObjectDataCD>(),
                ComponentType.ReadOnly<LocalTransform>());
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            NativeArray<ObjectDataCD> objectData = query.ToComponentDataArray<ObjectDataCD>(Allocator.Temp);
            NativeArray<LocalTransform> transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            int removed = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                ObjectID id = objectData[i].objectID;
                if (id != id0 && id != id1 && id != id2) continue;
                float3 p = transforms[i].Position;
                if (p.x != 0f || p.y != 0f || p.z != 0f) continue;
                em.DestroyEntity(entities[i]);
                removed++;
            }
            entities.Dispose();
            objectData.Dispose();
            transforms.Dispose();
            query.Dispose();
            if (removed > 0)
            {
                Debug.Log(Tag + "removed " + removed + " phantom cart entity(ies) at the world origin (v1.0.0 leftovers).");
            }
        }

        private static void AddRecipes(Entity forge, EntityManager entityManager)
        {
            if (!entityManager.HasBuffer<CanCraftObjectsBuffer>(forge))
            {
                Debug.LogWarning(Tag + "Railway Forge has no CanCraftObjectsBuffer - recipes NOT added.");
                return;
            }
            DynamicBuffer<CanCraftObjectsBuffer> recipes = entityManager.GetBuffer<CanCraftObjectsBuffer>(forge);
            int added = 0;
            foreach (Tier tier in Tiers)
            {
                ObjectID id = API.Authoring.GetObjectID(tier.ObjectName);
                if (id == ObjectID.None)
                {
                    Debug.LogWarning(Tag + tier.ObjectName + " has no ObjectID yet - recipe NOT added.");
                    continue;
                }
                bool registered = false;
                foreach (GameObject clone in _clones)
                {
                    if (clone != null && clone.name == tier.CloneName) { registered = true; break; }
                }
                if (!registered) continue;   // tier was refused at clone time - don't offer it
                bool present = false;
                for (int i = 0; i < recipes.Length; i++)
                {
                    if (recipes[i].objectID == id) { present = true; break; }
                }
                if (present) continue;
                CanCraftObjectsBuffer entry = new CanCraftObjectsBuffer
                {
                    objectID = id,
                    amount = 1,
                    entityAmountToConsume = 0,
                    allowCraftingNone = false,
                    craftingTimeOverride = 0f   // 0 = use the item's own craftingTime
                };
                if (recipes.Length > 0 && recipes[recipes.Length - 1].objectID == ObjectID.None)
                {
                    recipes[recipes.Length - 1] = entry;   // fill an empty trailing slot rather than appending after it
                }
                else
                {
                    recipes.Add(entry);
                }
                added++;
            }
            Debug.Log(Tag + "Railway Forge: +" + added + " recipe(s), " + recipes.Length + " total, world '" + entityManager.World.Name + "'");
        }
    }
}
