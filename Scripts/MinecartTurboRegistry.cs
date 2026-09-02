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
    /// ("MinecartTurbo:Mk1/2/3" = 2x/3x/4x) with maxSpeed and crafting cost scaled by its multiplier, registers the
    /// clones with the official mod API so the game converts them like any other object, and appends them to the
    /// Railway Forge's recipe list when the forge is converted.
    ///
    /// LOCALIZATION TRAP: I2's LocalizationManager.TryGetTranslation rewrites ':' to '_' in every looked-up term,
    /// but the CSV import keeps ':' as-is. Item name terms are "Items/" + objectName, so the CSV keys MUST use the
    /// underscore form (Items/MinecartTurbo_Mk1), never the object name's colon form.
    ///
    /// IDENTITY IS PERMANENT: the ObjectID is assigned at boot from the object name, and the NetCode ghost type is
    /// hashed from the clone's GameObject name. Renaming a tier orphans every cart of that tier already in a world.
    /// </summary>
    public static class MinecartTurboRegistry
    {
        private sealed class Tier
        {
            public string ObjectName;   // registered object name -> ObjectID + localization key "Items/<name>"
            public float Multiplier;    // applied to maxSpeed, every recipe ingredient and the sell value
            public Rarity Rarity;       // purely cosmetic: name colour in the UI marks the tier
            public string CloneName { get { return ObjectName.Replace(':', '_'); } }   // ghost type derives from this
        }

        private static readonly Tier[] Tiers =
        {
            new Tier { ObjectName = "MinecartTurbo:Mk1", Multiplier = 2.0f, Rarity = Rarity.Rare },
            new Tier { ObjectName = "MinecartTurbo:Mk2", Multiplier = 3.0f, Rarity = Rarity.Epic },
            new Tier { ObjectName = "MinecartTurbo:Mk3", Multiplier = 4.0f, Rarity = Rarity.Legendary },
        };

        private const string Tag = "[MinecartTurbo] ";

        private static bool _attempted;
        private static bool _subscribed;
        private static readonly List<GameObject> _clones = new List<GameObject>();

        /// <summary>Idempotent. Safe to call every frame; does real work exactly once, before ECSManager.Init runs.</summary>
        public static void TryRegister(string source)
        {
            if (_attempted) return;
            if (Manager.main == null) return;
            ECSManager ecs = Manager.ecs;
            if (ecs == null || ecs.pugDatabase == null || API.Authoring == null) return;
            if (ecs.BlobAssetStore.IsCreated)
            {
                // Init's body has already started/finished: registering now would add authoring objects that never
                // get converted. Refuse loudly instead of half-working.
                _attempted = true;
                Debug.LogError(Tag + "too late to register (" + source + "): ECSManager.Init already ran. Turbo minecarts NOT added.");
                return;
            }
            _attempted = true;
            try
            {
                Register(ecs, source);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "registration failed: " + e);
            }
        }

        private static void Register(ECSManager ecs, string source)
        {
            MonoBehaviour vanilla = FindVanillaMinecart(ecs.pugDatabase.prefabList);
            if (vanilla == null)
            {
                Debug.LogError(Tag + "vanilla Minecart authoring prefab not found in PugDatabase - turbo minecarts NOT added.");
                return;
            }
            Subscribe();
            foreach (Tier tier in Tiers)
            {
                GameObject clone = CloneCart(vanilla.gameObject, tier);
                if (clone == null) continue;
                API.Authoring.RegisterAuthoringGameObject(clone);
                _clones.Add(clone);
            }
            Debug.Log(Tag + "registered " + _clones.Count + " turbo minecart prefab(s) cloned from '" + vanilla.name + "' (" + source + ")");
        }

        private static MonoBehaviour FindVanillaMinecart(List<MonoBehaviour> prefabList)
        {
            if (prefabList == null) return null;
            foreach (MonoBehaviour mono in prefabList)
            {
                if (mono == null) continue;
                EntityMonoBehaviourData embd = mono as EntityMonoBehaviourData;
                if (embd != null)
                {
                    if (embd.objectInfo != null && embd.objectInfo.objectID == ObjectID.Minecart && embd.objectInfo.variation == 0) return mono;
                    continue;
                }
                ObjectAuthoring oa = mono as ObjectAuthoring;
                if (oa != null && oa.objectName == "Minecart" && oa.variation == 0) return mono;
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

            float baseSpeed = cart.maxSpeed;
            cart.maxSpeed = Mathf.Round(baseSpeed * tier.Multiplier);
            cart.currentSpeed = 0f;

            Debug.Log(Tag + tier.ObjectName + ": maxSpeed " + baseSpeed + " -> " + cart.maxSpeed + ", recipe " + recipeText
                      + ", rarity " + tier.Rarity + ", clone '" + clone.name + "'");
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
            if (info.prefabInfos != null && info.prefabInfos.Count > 0 && info.prefabInfos[0].prefab != null)
            {
                oa.graphicalPrefab = info.prefabInfos[0].prefab.gameObject;   // same visuals as the vanilla cart
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
                    item.requiredObjectsToCraft.Add(new InventoryItemAuthoring.CraftingObject
                    {
                        objectName = ingredient.objectID.ToString(),
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

        /// <summary>Fires once per converted object type (after all conversion). Hooks the Railway Forge.</summary>
        private static void OnObjectTypeAdded(Entity entity, GameObject authoring, EntityManager entityManager)
        {
            try
            {
                if (!entityManager.HasComponent<ObjectDataCD>(entity)) return;
                ObjectID id = entityManager.GetComponentData<ObjectDataCD>(entity).objectID;
                foreach (Tier tier in Tiers)
                {
                    if (authoring.name == tier.CloneName)
                    {
                        MakeProperPrefab(entity, entityManager, tier.ObjectName);
                        Debug.Log(Tag + tier.ObjectName + " converted as ObjectID " + (int)id + " in world '" + entityManager.World.Name + "'");
                    }
                }
                if (id == ObjectID.RailwayForge) AddRecipes(entity, entityManager);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "OnObjectTypeAdded failed: " + e);
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
        /// and collection registration treat the carts exactly like vanilla prefab assets.
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
