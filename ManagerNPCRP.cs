using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Norsemen;
using UnityEngine;
using System.Collections;
namespace NorsemanNpcRp
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("RustyMods.Norsemen", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.kylet.livingnorsemen", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("expand_world_prefabs", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class ManagerNPCRP : BaseUnityPlugin
    {
        public const string PluginGuid = "Norseman.NPC.RP";
        public const string PluginName = "Norseman NPC RP";
        public const string PluginVersion = "0.1.0";
        internal static ManagerNPCRP Instance { get; private set; }
        private const string ConfigFolderName = "Norseman-NPC-RP";
        private const string DatabaseFileName = "npc_database.json";
        private const int SupportedSchemaVersion = 3;
        private const string RpIdKey = "NorsemanNPC_RP_ID";
        private const string ClanKey = "NorsemanNPC_Clan";
        private const string RoleKey = "NorsemanNPC_Role";
        private const string ProfessionKey = "NorsemanNPC_Profession";
        private const string EquipmentAppliedKey = "NorsemanNPC_EquipmentApplied";
        private const string LivingVillagerIdKey = "LivingNorsemen_VillagerID";
        private readonly Dictionary<string, NpcDefinition> _npcs =
            new Dictionary<string, NpcDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Norseman> _subscribedNorsemen = new HashSet<Norseman>();
        private Harmony _harmony;
        private ManualLogSource _log;
        private string _databasePath;
        private NpcDatabase _database;
        private readonly Dictionary<string, Faction> _factionCache =
            new Dictionary<string, Faction>(StringComparer.OrdinalIgnoreCase);
        private bool _autoSpawnDone;
        private ConfigEntry<KeyCode> _talkKey;
        internal NpcDatabase Database { get { return _database; } }
        internal Dictionary<string, NpcDefinition> Npcs { get { return _npcs; } }
        internal ManualLogSource Log { get { return _log; } }
        internal bool SaveDatabaseInternal() { return SaveDatabase(); }
        private static readonly FieldInfo VikingFactionField =
            AccessTools.Field(typeof(Character), "m_faction");
        private void Awake()
        {
            Instance = this;
            _log = Logger;
            _databasePath = Path.Combine(Paths.ConfigPath, ConfigFolderName, DatabaseFileName);
            _harmony = new Harmony(PluginGuid);
            LoadDatabase();
            // Phase 3: attach to Norsemen prefab-definition creation.
            MethodInfo createMethod = AccessTools.Method(typeof(Norseman), "Create");
            if (createMethod != null)
            {
                _harmony.Patch(
                    createMethod,
                    prefix: new HarmonyMethod(typeof(ManagerNPCRP), nameof(NorsemanCreatePrefix)));
            }
            // Instance-side hook: runs when a spawned Norseman GameObject is initialized.
            MethodInfo vikingAwake = AccessTools.Method(typeof(Viking), "Awake");
            if (vikingAwake != null)
            {
                _harmony.Patch(
                    vikingAwake,
                    postfix: new HarmonyMethod(typeof(ManagerNPCRP), nameof(VikingAwakePostfix)));
            }
            MethodInfo zoneStart = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (zoneStart != null)
            {
                _harmony.Patch(
                    zoneStart,
                    postfix: new HarmonyMethod(typeof(ManagerNPCRP), nameof(ZoneSystemStartPostfix)));
            }
            _talkKey = Config.Bind("Interaction", "TalkKey", KeyCode.T,
                "Key to talk to the nearest RP NPC (clues depend on reputation and story flags).");
            try
            {
                MethodInfo onDeath = AccessTools.Method(typeof(Character), "OnDeath");
                if (onDeath != null)
                    _harmony.Patch(onDeath, postfix: new HarmonyMethod(typeof(ReputationManager),
                        nameof(ReputationManager.CharacterOnDeathPostfix)));
                MethodInfo interact = AccessTools.Method(typeof(Viking), "Interact");
                if (interact != null)
                    _harmony.Patch(interact, prefix: new HarmonyMethod(typeof(ReputationManager),
                        nameof(ReputationManager.VikingInteractPrefix)));
                MethodInfo gameStart = AccessTools.Method(typeof(Game), "Start");
                if (gameStart != null)
                    _harmony.Patch(gameStart, postfix: new HarmonyMethod(typeof(ReputationManager),
                        nameof(ReputationManager.GameStartPostfix)));
            }
            catch (Exception ex)
            {
                _log.LogWarning("Reputation patches partly unavailable: " + ex.Message);
            }
            RegisterCommands();
            _log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }
        private void Update()
        {
            if (_talkKey != null && Input.GetKeyDown(_talkKey.Value))
                NpcTalk.TryTalk();
            ReputationManager.ClientTick();
        }
        private void OnDestroy()
        {
            try
            {
                _harmony?.UnpatchSelf();
            }
            catch
            {
                // Do not let plugin shutdown fail because of an unpatch problem.
            }
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }
        // ---------------------------------------------------------------------
        // PHASE 1 - JSON
        // ---------------------------------------------------------------------
        public bool LoadDatabase()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath));

                if (!File.Exists(_databasePath))
                {
                    WriteTemplateDatabase();
                    _log.LogWarning($"Database not found. Template created: {_databasePath}");
                }
                string json = File.ReadAllText(_databasePath);
                NpcDatabase database = JsonConvert.DeserializeObject<NpcDatabase>(json);
                if (database == null)
                    throw new InvalidDataException("JSON deserialized to null.");
                bool migrated = false;
                if (database.schemaVersion == 2)
                {
                    PopulationManager.MigrateToV3(database);
                    database.schemaVersion = 3;
                    migrated = true;
                }
                _database = database;
                bool valid = ValidateDatabase(database);
                if (!valid)
                {
                    _log.LogError("Database validation failed. No NPC will be spawned.");
                    _npcs.Clear();
                    return false;
                }
                _npcs.Clear();
                foreach (KeyValuePair<string, NpcDefinition> pair in database.npcs)
                    _npcs.Add(pair.Key, pair.Value);
                BuildFactionCache(database);
                if (migrated)
                {
                    File.Copy(_databasePath, _databasePath + ".v2.bak", true);
                    SaveDatabase();
                    _log.LogInfo("Database migrated from schema 2 to 3 (backup: npc_database.json.v2.bak).");
                }
                _log.LogInfo(
                    $"Database READY: schema={database.schemaVersion}, " +
                    $"factions={database.factions.Count}, clans={database.clans.Count}, " +
                    $"equipmentSets={database.equipmentSets.Count}, " +
                    $"dialogueSets={database.dialogueSets.Count}, npcs={_npcs.Count}");

                return true;
            }
            catch (Exception ex)
            {
                _database = null;
                _npcs.Clear();
                _log.LogError($"Failed to load database: {ex}");
                return false;
            }
        }
        private bool ValidateDatabase(NpcDatabase db)
        {
            bool ok = true;
            if (db.schemaVersion != SupportedSchemaVersion)
            {
                _log.LogError(
                    $"Unsupported schemaVersion: {db.schemaVersion} (expected {SupportedSchemaVersion})");
                ok = false;
            }
            if (db.factions == null || db.clans == null || db.equipmentSets == null ||
                db.dialogueSets == null || db.npcs == null)
            {
                _log.LogError("One or more root database sections are missing.");
                return false;
            }
            foreach (KeyValuePair<string, FactionDefinition> fac in db.factions)
            {
                if (fac.Value != null && !string.IsNullOrWhiteSpace(fac.Value.aggressiveVariant) &&
                    !db.factions.ContainsKey(fac.Value.aggressiveVariant))
                {
                    _log.LogError(
                        $"Faction '{fac.Key}' references unknown aggressiveVariant " +
                        $"'{fac.Value.aggressiveVariant}'.");
                    ok = false;
                }
            }
            foreach (KeyValuePair<string, ClanDefinition> clan in db.clans)
            {
                if (string.IsNullOrWhiteSpace(clan.Key))
                {
                    _log.LogError("A clan has an empty key.");
                    ok = false;
                    continue;
                }
                if (clan.Value == null || string.IsNullOrWhiteSpace(clan.Value.faction))
                {
                    _log.LogError($"Clan '{clan.Key}' has no faction.");
                    ok = false;
                    continue;
                }
                if (!db.factions.ContainsKey(clan.Value.faction))
                {
                    _log.LogError(
                        $"Clan '{clan.Key}' references unknown faction '{clan.Value.faction}'.");
                    ok = false;
                }
                if (!string.IsNullOrWhiteSpace(clan.Value.defaultDialogueSet) &&
                    !db.dialogueSets.ContainsKey(clan.Value.defaultDialogueSet))
                {
                    _log.LogError(
                        $"Clan '{clan.Key}' references unknown dialogue set " +
                        $"'{clan.Value.defaultDialogueSet}'.");
                    ok = false;
                }
            }
            foreach (KeyValuePair<string, EquipmentSetDefinition> set in db.equipmentSets)
            {
                if (set.Value == null)
                {
                    _log.LogError($"Equipment set '{set.Key}' is null.");
                    ok = false;
                }
            }
            foreach (KeyValuePair<string, NpcDefinition> pair in db.npcs)
            {
                string id = pair.Key;
                NpcDefinition npc = pair.Value;
                if (npc == null)
                {
                    _log.LogError($"NPC '{id}' is null.");
                    ok = false;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(id))
                {
                    _log.LogError("An NPC has an empty ID.");
                    ok = false;
                }
                if (npc.identity == null || string.IsNullOrWhiteSpace(npc.identity.name))
                {
                    _log.LogError($"NPC '{id}' has no identity.name.");
                    ok = false;
                }
                if (string.IsNullOrWhiteSpace(npc.prefab))
                {
                    _log.LogError($"NPC '{id}' has no prefab.");
                    ok = false;
                }
                if (string.IsNullOrWhiteSpace(npc.clan) || !db.clans.ContainsKey(npc.clan))
                {
                    _log.LogError($"NPC '{id}' references unknown clan '{npc.clan}'.");
                    ok = false;
                }
                if (!string.IsNullOrWhiteSpace(npc.faction) && npc.clan != null &&
                    db.clans.TryGetValue(npc.clan, out ClanDefinition npcClan) &&
                    npcClan != null && npcClan.faction != npc.faction)
                {
                    _log.LogWarning(
                        $"NPC '{id}': faction '{npc.faction}' differs from clan faction " +
                        $"'{npcClan.faction}' (clan faction wins).");
                }
                if (!string.IsNullOrWhiteSpace(npc.equipmentSet) &&
                    !db.equipmentSets.ContainsKey(npc.equipmentSet))
                {
                    _log.LogError(
                        $"NPC '{id}' references unknown equipment set '{npc.equipmentSet}'.");
                    ok = false;
                }
                if (npc.dialogue != null && !string.IsNullOrWhiteSpace(npc.dialogue.set) &&
                    !db.dialogueSets.ContainsKey(npc.dialogue.set))
                {
                    _log.LogError(
                        $"NPC '{id}' references unknown dialogue set '{npc.dialogue.set}'.");
                    ok = false;
                }
            }
            return ok;
        }
        private void WriteTemplateDatabase()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath));

            var template = new NpcDatabase
            {
                schemaVersion = SupportedSchemaVersion,
                settings = new DatabaseSettings
                {
                    enabled = true,
                    spawnOnWorldLoad = false,
                    preventDuplicates = true,
                    respawnIfDead = false,
                    debug = true,
                    snapToGround = true,
                    spawnHeightOffset = 0.5f
                },
                factions = new Dictionary<string, FactionDefinition>(),
                clans = new Dictionary<string, ClanDefinition>(),
                equipmentSets = new Dictionary<string, EquipmentSetDefinition>(),
                dialogueSets = new Dictionary<string, DialogueSetDefinition>(),
                npcs = new Dictionary<string, NpcDefinition>()
            };
            File.WriteAllText(
                _databasePath,
                JsonConvert.SerializeObject(template, Formatting.Indented));
        }
        // ---------------------------------------------------------------------
        // PHASE 2 - ZDO SPAWNER
        // ---------------------------------------------------------------------
        public int SpawnAllConfiguredNPCs(bool force = false)
        {
            if (_database == null || !_database.settings.enabled)
                return 0;
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                _log.LogWarning("spawn_rp_npcs must be executed by the server/host.");
                return 0;
            }
            if (ZDOMan.instance == null || ZNetScene.instance == null)
            {
                _log.LogWarning("ZDOMan or ZNetScene is not ready.");
                return 0;
            }
            Dictionary<string, List<ZDO>> existing = CollectZdosById();
            int created = 0, replaced = 0, skippedExisting = 0, skippedDead = 0, failed = 0, duplicated = 0;
            foreach (KeyValuePair<string, NpcDefinition> pair in _npcs)
            {
                string id = pair.Key;
                NpcDefinition npc = pair.Value;
                if (npc.locations == null || npc.locations.spawn == null || !npc.locations.spawn.enabled)
                    continue;
                List<ZDO> found;
                existing.TryGetValue(id, out found);
                if (found != null && found.Count > 1)
                {
                    duplicated++;
                    _log.LogWarning($"[{id}] {found.Count} ZDOs share this RP id (rp_audit fix removes the extras).");
                }
                if (found != null && found.Count > 0)
                {
                    if (force)
                    {
                        // force = REPLACE, never add a second copy.
                        foreach (ZDO z in found) ZDOMan.instance.DestroyZDO(z);
                        replaced++;
                    }
                    else if (_database.settings.preventDuplicates)
                    {
                        skippedExisting++;
                        continue;
                    }
                }
                else if (!force && NpcLifecycle.IsAwaitingRespawn(id))
                {
                    skippedDead++;   // died recently: the respawn timer decides, not a world reload
                    continue;
                }
                if (SpawnNpcZdo(id, npc))
                {
                    created++;
                    NpcLifecycle.ClearDead(id);
                }
                else
                {
                    failed++;
                }
            }
            NpcLifecycle.MarkEverSpawned();
            _log.LogInfo($"spawn_rp_npcs{(force ? " (force)" : "")}: {created} created, {replaced} replaced, " +
                         $"{skippedExisting} already in world, {skippedDead} awaiting respawn, {failed} failed, " +
                         $"{duplicated} id(s) with duplicates.");
            return created;
        }
        private static readonly HashSet<string> ReportedPrefabs = new HashSet<string>();
        private bool SpawnNpcZdo(string id, NpcDefinition npc)
        {
            SpawnPosition p = npc.locations.spawn;
            int prefabHash = StringExtensionMethods.GetStableHashCode(npc.prefab);
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
            if (prefab == null)
            {
                if (ReportedPrefabs.Add(npc.prefab))
                    _log.LogError($"Prefab not found in ZNetScene: {npc.prefab} (first seen on [{id}]; same prefab = same failure)");
                return false;
            }
            ZNetView prefabView = prefab.GetComponent<ZNetView>();
            if (prefabView == null)
            {
                _log.LogError($"[{id}] Prefab '{npc.prefab}' has no ZNetView.");
                return false;
            }
            Vector3 position = new Vector3(p.x, p.y, p.z);
            if (float.IsNaN(position.x) || float.IsNaN(position.z) || float.IsInfinity(position.x + position.z))
            {
                _log.LogError($"[{id}] invalid spawn position.");
                return false;
            }
            if (p.baked)
                position.y = p.y + 0.15f;
            else if (_database.settings.snapToGround)
                position.y = ResolveGroundHeight(position) + _database.settings.spawnHeightOffset;
            Vector3 rotation = new Vector3(0f, p.rotationY, 0f);
            ZDO zdo = ZDOMan.instance.CreateNewZDO(position, prefabHash);
            if (zdo == null)
            {
                _log.LogError($"[{id}] CreateNewZDO returned null.");
                return false;
            }
            ZdoAccess.SetPrefab(zdo, prefabHash);
            ZdoAccess.SetRotation(zdo, rotation);
            // RP NPCs must survive restarts and zone unloads whatever the prefab says
            // (a non-persistent ZDO is destroyed as soon as nobody owns it).
            zdo.Persistent = true;
            if (!prefabView.m_persistent)
                _log.LogDebug($"[{id}] prefab '{npc.prefab}' is not persistent by default: forced persistent.");
            zdo.Distant = prefabView.m_distant;
            zdo.Type = prefabView.m_type;
            // Our persistent identity.
            zdo.Set(RpIdKey, id);
            zdo.Set(ClanKey, npc.clan ?? "");
            zdo.Set(RoleKey, npc.role ?? "");
            if (npc.livingNorsemen != null)
                zdo.Set(ProfessionKey, npc.livingNorsemen.profession ?? "");
            ApplyLivingNorsemenZdoData(zdo, npc);
            _log.LogInfo(
                $"[{id}] ZDO created: prefab={npc.prefab}, " +
                $"position={position}, persistent={zdo.Persistent} (prefab default {prefabView.m_persistent})");

            return true;
        }
        private float PointY(SpawnPosition p)
        {
            if (p.baked)
                return p.y + 0.15f;

            if (_database != null && _database.settings.snapToGround)
                return ResolveGroundHeight(new Vector3(p.x, p.y, p.z)) +
                       _database.settings.spawnHeightOffset;
            return p.y;
        }
        private void ApplyLivingNorsemenZdoData(ZDO zdo, NpcDefinition npc)
        {
            if (npc.locations == null)
                return;
            // Static NPCs stay at their post: no routine data for LivingNorsemen.
            if (npc.behavior != null && npc.behavior.isStatic)
                return;
            if (npc.locations.home != null && npc.locations.home.enabled)
            {
                zdo.Set("LivingNorsemen_HasHome", true);
                zdo.Set("LivingNorsemen_HomeX", npc.locations.home.x);
                zdo.Set("LivingNorsemen_HomeY", PointY(npc.locations.home));
                zdo.Set("LivingNorsemen_HomeZ", npc.locations.home.z);
            }

            if (npc.locations.work != null && npc.locations.work.enabled)
            {
                zdo.Set("LivingNorsemen_HasWork", true);
                zdo.Set("LivingNorsemen_WorkX", npc.locations.work.x);
                zdo.Set("LivingNorsemen_WorkY", PointY(npc.locations.work));
                zdo.Set("LivingNorsemen_WorkZ", npc.locations.work.z);
            }
            if (npc.locations.patrolPointB != null && npc.locations.patrolPointB.enabled)
            {
                zdo.Set("LivingNorsemen_HasPatrolPointB", true);
                zdo.Set("LivingNorsemen_PatrolPointBX", npc.locations.patrolPointB.x);
                zdo.Set("LivingNorsemen_PatrolPointBY", PointY(npc.locations.patrolPointB));
                zdo.Set("LivingNorsemen_PatrolPointBZ", npc.locations.patrolPointB.z);
            }
        }
        private void ApplyStaticBehavior(Viking viking, ZDO zdo, NpcDefinition npc)
        {
            if (npc.behavior == null || !npc.behavior.isStatic)
                return;
            const string key = "NorsemanNPC_StaticApplied";
            if (zdo.GetBool(key, false))
                return;
            MonsterAI ai = viking.GetComponent<MonsterAI>();
            if (ai != null)
                ai.SetPatrolPoint(); // anchors the NPC at its current (spawn) position
            zdo.Set(key, true);
        }
        internal bool RespawnNamed(string id)
        {
            NpcDefinition npc;
            if (_database == null || !_npcs.TryGetValue(id, out npc)) return false;
            if (npc.locations == null || npc.locations.spawn == null || !npc.locations.spawn.enabled) return false;
            if (ZDOMan.instance == null || ZNetScene.instance == null) return false;
            if (FindNpcZdo(id) != null) return true;
            return SpawnNpcZdo(id, npc);
        }
        internal Dictionary<string, List<ZDO>> CollectZdosById()
        {
            Dictionary<string, List<ZDO>> map = new Dictionary<string, List<ZDO>>(StringComparer.OrdinalIgnoreCase);
            if (ZDOMan.instance == null)
                return map;
            foreach (ZDO zdo in ZdoAccess.All())
            {
                if (zdo == null || !zdo.IsValid())
                    continue;
                string id = zdo.GetString(RpIdKey, "");
                if (string.IsNullOrEmpty(id))
                    continue;
                List<ZDO> list;
                if (!map.TryGetValue(id, out list)) map[id] = list = new List<ZDO>();
                list.Add(zdo);
            }
            return map;
        }
        // Zone may not be loaded on the server: fall back to the world generator.
        internal static float ResolveGroundHeight(Vector3 p)
        {
            float h;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(p, out h))
                return h;
            if (WorldGenerator.instance != null)
                return WorldGenerator.instance.GetHeight(p.x, p.z);
            return p.y;
        }
        private ZDO FindNpcZdo(string id)
        {
            if (ZDOMan.instance == null)
                return null;
            foreach (ZDO zdo in ZdoAccess.All())
            {
                if (zdo == null || !zdo.IsValid())
                    continue;
                if (string.Equals(zdo.GetString(RpIdKey, ""), id, StringComparison.OrdinalIgnoreCase))
                    return zdo;
            }
            return null;
        }
        // ---------------------------------------------------------------------
        // PHASE 3 - INSTANCE INTERCEPTOR
        // ---------------------------------------------------------------------
        private static void NorsemanCreatePrefix(Norseman __instance)
        {
            if (Instance == null || __instance == null)
                return;
            if (Instance._subscribedNorsemen.Add(__instance))
            {
                __instance.OnCreated += Instance.OnNorsemanPrefabCreated;
            }
        }
        private void OnNorsemanPrefabCreated(Norseman norseman)
        {
            if (norseman == null)
                return;
            _log.LogInfo($"Norsemen prefab ready: {norseman.name}");
        }
        private static void ZoneSystemStartPostfix()
        {
            if (Instance != null)
                Instance._autoSpawnDone = false;   // a new world load must be able to auto-spawn again
            Instance.StartCoroutine(Instance.AutoSpawnRoutine());
            Instance.StartCoroutine(NpcLifecycle.Run(Instance));
            if (Instance != null)
                Instance.StartCoroutine(PopulationManager.Run(Instance));
            if (Instance != null)
                Instance.StartCoroutine(ReputationManager.Run(Instance));
        }
        private System.Collections.IEnumerator AutoSpawnRoutine()
        {
            if (_database == null || !_database.settings.enabled ||
                !_database.settings.spawnOnWorldLoad || _autoSpawnDone)
                yield break;
            // Wait until the world is really up (max ~60 s).
            for (int i = 0; i < 120; i++)
            {
                if (ZNet.instance != null && ZDOMan.instance != null && ZNetScene.instance != null)
                    break;
                yield return new WaitForSeconds(0.5f);
            }
            if (ZNet.instance == null || !ZNet.instance.IsServer())
                yield break;
            yield return new WaitForSeconds(5f);
            _autoSpawnDone = true;
            SpawnAllConfiguredNPCs(false);
        }
        private static void VikingAwakePostfix(Viking __instance)
        {
            if (Instance == null || __instance == null)
                return;
            Instance.ApplyToViking(__instance);
        }
        private void ApplyToViking(Viking viking)
        {
            try
            {
                ZNetView nview = viking.GetComponent<ZNetView>();
                if (nview == null || nview.GetZDO() == null)
                    return;
                ZDO zdo = nview.GetZDO();
                string id = zdo.GetString(RpIdKey, "");
                if (string.IsNullOrWhiteSpace(id))
                    return;
                NpcDefinition npc;
                if (!_npcs.TryGetValue(id, out npc) &&
                    !PopulationManager.TryResolveGenerated(zdo, id, out npc))
                {
                    _log.LogWarning($"Spawned RP NPC references unknown database ID '{id}'.");
                    return;
                }
                // Identity: displayed Norsemen name is stored in the normal tamed-name ZDO field.
                if (nview.IsOwner())
                {
                    zdo.Set(ZDOVars.s_tamedName, npc.identity.name);
                    zdo.Set(RpIdKey, id);
                    zdo.Set(ClanKey, npc.clan ?? "");
                    zdo.Set(RoleKey, npc.role ?? "");
                    if (npc.livingNorsemen != null)
                        zdo.Set(ProfessionKey, npc.livingNorsemen.profession ?? "");
                    ApplyLivingNorsemenZdoData(zdo, npc);
                    ApplyEquipment(viking, npc);
                    ApplyStaticBehavior(viking, zdo, npc);
                    // Real ground/floor/water/obstacle check once the zone colliders exist.
                    PlacementSettings placement = _database.settings.placement;
                    if (placement != null && placement.runtime &&
                        !(npc.locations != null && npc.locations.spawn != null && npc.locations.spawn.baked) &&
                        !zdo.GetBool(NpcPlacement.PlacedKey, false))
                    {
                        StartCoroutine(NpcPlacement.PlaceWhenReady(viking, nview, npc, placement));
                    }
                }
                // m_faction is a local field (not synced by ZDO): every client must set it.
                ApplyFaction(viking, npc);
                // Dialogue text is local too; re-applied once later in case Norsemen overwrites it.
                ApplyDialogue(viking, npc);
                StartCoroutine(ReapplyDialogueLater(viking, npc));
                PopulationManager.AttachWanderer(viking, nview, zdo, npc, id);
                if (_database.settings.debug)
                    _log.LogInfo(
                        $"[{id}] applied: name='{npc.identity.name}', " +
                        $"clan='{npc.clan}', role='{npc.role}', prefab='{npc.prefab}'.");
            }
            catch (Exception ex)
            {
                _log.LogError($"Failed to apply RP data to Viking: {ex}");
            }
        }
        private void BuildFactionCache(NpcDatabase db)
        {
            _factionCache.Clear();
            foreach (KeyValuePair<string, FactionDefinition> f in db.factions)
            {
                FactionManager.customFactions.TryGetValue(
                    FactionManager.GetFaction(f.Key), out Faction existing);
                _factionCache[f.Key] = existing ?? new Faction(f.Key, f.Value.isFriendly);
            }
        }
        private void ApplyFaction(Viking viking, NpcDefinition npc)
        {
            if (_database == null || npc == null || string.IsNullOrWhiteSpace(npc.clan))
                return;
            ClanDefinition clan;
            if (!_database.clans.TryGetValue(npc.clan, out clan) ||
                string.IsNullOrWhiteSpace(clan.faction))
                return;
            string factionKey = clan.faction;
            // Opt-in hostility: only NPCs flagged "aggressive" use the hostile variant.
            FactionDefinition factionDef;
            if (((npc.behavior != null && npc.behavior.aggressive) || ReputationManager.GuardsHostile(npc)) &&
                _database.factions.TryGetValue(clan.faction, out factionDef) &&
                factionDef != null && !string.IsNullOrWhiteSpace(factionDef.aggressiveVariant))
            {
                factionKey = factionDef.aggressiveVariant;
            }
            Faction faction;
            if (!_factionCache.TryGetValue(factionKey, out faction))
                return;
            if (VikingFactionField != null)
                VikingFactionField.SetValue(viking, faction.faction);
            else
                _log.LogWarning("Could not locate Character.m_faction; faction assignment skipped.");
        }
        private void ApplyEquipment(Viking viking, NpcDefinition npc)
        {
            if (npc.equipmentSet == null || _database == null)
                return;
            EquipmentSetDefinition set;
            if (!_database.equipmentSets.TryGetValue(npc.equipmentSet, out set) ||
                set == null || !string.Equals(set.mode, "fixed", StringComparison.OrdinalIgnoreCase))
                return;
            ZNetView nview = viking.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null || !nview.IsOwner())
                return;
            ZDO zdo = nview.GetZDO();
            if (zdo.GetBool(EquipmentAppliedKey, false))
            {
                // The flag is saved with the ZDO; check the items survived the reload too.
                Inventory inv = viking.GetInventory();
                if (inv == null || inv.NrOfItems() > 0)
                    return;
                _log.LogInfo($"[{npc.identity.name}] inventory empty after reload: equipment re-applied.");
            }
            List<string> itemNames = new List<string>();
            if (set.weapons != null) itemNames.AddRange(set.weapons);
            if (set.armor != null) itemNames.AddRange(set.armor);
            if (set.tools != null) itemNames.AddRange(set.tools);
            foreach (string itemName in itemNames.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                try
                {
                    GameObject prefab = ObjectDB.instance != null
                        ? ObjectDB.instance.GetItemPrefab(itemName)
                        : null;

                    if (prefab == null)
                    {
                        _log.LogWarning(
                            $"[{npc.identity.name}] Equipment prefab not found: {itemName}");
                        continue;
                    }

                    viking.GetInventory().AddItem(itemName, 1, 1, 0, 0L, "", false, false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        $"[{npc.identity.name}] Failed to add equipment '{itemName}': {ex.Message}");
                }
            }
            viking.EquipItems();
            zdo.Set(EquipmentAppliedKey, true);
            _log.LogInfo(
                $"[{npc.identity.name}] equipment set '{npc.equipmentSet}' applied.");
        }
        // ---------------------------------------------------------------------
        // DIALOGUES (reflection: Norsemen's dialogue fields are not part of a public API)
        // ---------------------------------------------------------------------
        private static readonly Dictionary<string, string[]> DialogueAliases =
            new Dictionary<string, string[]>
            {
                { "Greets", new[] { "greets", "greet", "randomgreets" } },
                { "Farewells", new[] { "farewells", "farewell", "goodbye", "randomgoodbye" } },
                { "Generic", new[] { "generic", "randomtalk" } },
                { "PlayerBase", new[] { "playerbase", "randomtalkinplayerbase" } },
                { "Damaged", new[] { "damaged", "aggravated" } },
                { "Thieved", new[] { "thieved", "stolen", "privateareaalarm" } },
                { "Puke", new[] { "puke", "vomit" } },
                { "Eat", new[] { "eat", "eating" } }
            };
        private bool _dialogueDiscoveryLogged;
        private System.Collections.IEnumerator ReapplyDialogueLater(Viking viking, NpcDefinition npc)
        {
            yield return new WaitForSeconds(2f);
            // Re-evaluate story stages every 15 s; lines are only re-applied when a flag changed.
            string last = null;
            while (viking != null)
            {
                DialogueSetDefinition set = ResolveDialogueSet(npc);
                string signature = (set != null ? DialogueStages.Signature(set) : "") + "|" +
                                   ReputationManager.StateSignature(ReputationManager.IdOf(viking), npc);
                if (last == null || signature != last)
                {
                    ApplyFaction(viking, npc);
                    ApplyDialogue(viking, npc);
                    last = signature;
                }
                yield return new WaitForSeconds(15f);
            }
        }
        internal DialogueSetDefinition ResolveDialogueSet(NpcDefinition npc)
        {
            string setName = npc.dialogue != null ? npc.dialogue.set : null;

            ClanDefinition clan;
            if (string.IsNullOrWhiteSpace(setName) && npc.clan != null &&
                _database.clans.TryGetValue(npc.clan, out clan))
                setName = clan.defaultDialogueSet;

            DialogueSetDefinition set;
            if (string.IsNullOrWhiteSpace(setName) ||
                !_database.dialogueSets.TryGetValue(setName, out set))
                return null;

            return set;
        }
        private void ApplyDialogue(Viking viking, NpcDefinition npc)
        {
            if (_database == null)
                return;
            DialogueSetDefinition set = ResolveDialogueSet(npc);
            if (set == null)
                return;
            Dictionary<string, List<string>> lists = DialogueStages.Effective(set);
            // Grudge (killed NPC) or hostile clan: the NPC only speaks refusal lines.
            ReputationManager.Overlay(lists, ReputationManager.IdOf(viking), npc.clan);
            int applied = 0;
            List<string> discovered = new List<string>();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                 BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (Component comp in viking.GetComponentsInChildren<Component>(true))
            {
                if (comp == null)
                    continue;

                for (Type t = comp.GetType();
                     t != null && t != typeof(MonoBehaviour) && t != typeof(Component) &&
                     t != typeof(object);
                     t = t.BaseType)
                {
                    foreach (FieldInfo f in t.GetFields(flags))
                    {
                        bool isList = f.FieldType == typeof(List<string>);
                        bool isArray = f.FieldType == typeof(string[]);
                        if (!isList && !isArray)
                            continue;

                        discovered.Add($"{t.Name}.{f.Name}");
                        string norm = f.Name.ToLowerInvariant().Replace("m_", "").Replace("_", "");

                        foreach (KeyValuePair<string, string[]> alias in DialogueAliases)
                        {
                            if (Array.IndexOf(alias.Value, norm) < 0)
                                continue;

                            List<string> source;
                            if (!lists.TryGetValue(alias.Key, out source) ||
                                source == null || source.Count == 0)
                                continue;

                            f.SetValue(comp, isList
                                ? (object)new List<string>(source)
                                : source.ToArray());
                            applied++;
                        }
                    }
                }
            }
            if (_database.settings.debug && !_dialogueDiscoveryLogged)
            {
                _dialogueDiscoveryLogged = true;
                _log.LogInfo(
                    $"Dialogue: {applied} field(s) mapped on '{viking.name}'. " +
                    $"String-list fields found: {string.Join(", ", discovered.ToArray())}");
                if (applied == 0)
                    _log.LogWarning(
                        "No dialogue field matched. Send this field list so aliases can be adjusted.");
            }
        }
        // ---------------------------------------------------------------------
        // SAVE (used by in-game editing commands)
        // ---------------------------------------------------------------------
        private bool SaveDatabase()
        {
            try
            {
                if (File.Exists(_databasePath))
                    File.Copy(_databasePath, _databasePath + ".bak", true);

                File.WriteAllText(
                    _databasePath,
                    JsonConvert.SerializeObject(_database, Formatting.Indented));
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError($"Failed to save database: {ex}");
                return false;
            }
        }
        // ---------------------------------------------------------------------
        // COMMANDS
        // ---------------------------------------------------------------------
        private void RegisterCommands()
        {
            new NorseCommand(
                "validate_rp_npcs",
                "Validate Norseman-NPC-RP JSON database.",
                args =>
                {
                    bool ok = LoadDatabase();
                    if (Player.m_localPlayer != null)
                        Player.m_localPlayer.Message(
                            MessageHud.MessageType.TopLeft,
                            ok ? "Norseman-NPC-RP: database OK"
                               : "Norseman-NPC-RP: database INVALID");
                    return true;
                },
                adminOnly: true);
            new NorseCommand(
                "reload_rp_npcs",
                "Reload Norseman-NPC-RP JSON database.",
                args =>
                {
                    bool ok = LoadDatabase();
                    if (Player.m_localPlayer != null)
                        Player.m_localPlayer.Message(
                            MessageHud.MessageType.TopLeft,
                            ok ? "Norseman-NPC-RP: database reloaded"
                               : "Norseman-NPC-RP: reload failed");
                    return true;
                },
                adminOnly: true);
            new NorseCommand(
                "spawn_rp_npcs",
                "Spawn configured Norseman-NPC-RP NPCs.",
                args =>
                {
                    int count = SpawnAllConfiguredNPCs(false);
                    if (Player.m_localPlayer != null)
                        Player.m_localPlayer.Message(
                            MessageHud.MessageType.TopLeft,
                            $"Norseman-NPC-RP: {count} NPC ZDO(s) created.");
                    return true;
                },
                adminOnly: true);
            new NorseCommand(
                "spawn_rp_npcs_force",
                "Force creation of configured Norseman-NPC-RP NPCs.",
                args =>
                {
                    int count = SpawnAllConfiguredNPCs(true);
                    if (Player.m_localPlayer != null)
                        Player.m_localPlayer.Message(
                            MessageHud.MessageType.TopLeft,
                            $"Norseman-NPC-RP: {count} NPC ZDO(s) force-created.");
                    return true;
                },
                adminOnly: true);
            new NorseCommand(
                "rp_set_point",
                "Save your position as an NPC point: rp_set_point NPC-001 home|work|patrolB|spawn",
                args =>
                {
                    string id = args.Args.Length > 1 ? args.Args[1] : "";
                    string kind = args.Args.Length > 2 ? args.Args[2].ToLowerInvariant() : "";

                    NpcDefinition npc;
                    if (Player.m_localPlayer == null || !_npcs.TryGetValue(id, out npc))
                    {
                        _log.LogWarning("Usage: rp_set_point NPC-001 home|work|patrolB|spawn");
                        return true;
                    }

                    Vector3 pos = Player.m_localPlayer.transform.position;
                    SpawnPosition point = new SpawnPosition
                    {
                        enabled = true,
                        x = pos.x,
                        y = pos.y,
                        z = pos.z,
                        rotationY = Player.m_localPlayer.transform.eulerAngles.y
                    };

                    if (npc.locations == null)
                        npc.locations = new LocationsDefinition();

                    switch (kind)
                    {
                        case "home": npc.locations.home = point; break;
                        case "work": npc.locations.work = point; break;
                        case "patrolb": npc.locations.patrolPointB = point; break;
                        case "spawn": npc.locations.spawn = point; break;
                        default:
                            _log.LogWarning($"Unknown point type '{kind}'.");
                            return true;
                    }

                    bool saved = SaveDatabase();
                    Player.m_localPlayer.Message(
                        MessageHud.MessageType.TopLeft,
                        saved ? $"{id}: {kind} saved" : $"{id}: save FAILED");
                    return true;
                },
                adminOnly: true);
            new NorseCommand(
                "rp_tp",
                "Teleport to an NPC spawn point: rp_tp NPC-001",
                args =>
                {
                    string id = args.Args.Length > 1 ? args.Args[1] : "";
                    NpcDefinition npc;
                    if (Player.m_localPlayer == null || !_npcs.TryGetValue(id, out npc) ||
                        npc.locations == null || npc.locations.spawn == null)
                    {
                        _log.LogWarning("Usage: rp_tp NPC-001");
                        return true;
                    }

                    SpawnPosition sp = npc.locations.spawn;
                    Vector3 pos = new Vector3(sp.x, 0f, sp.z);
                    pos.y = ResolveGroundHeight(pos) + 2f;
                    Player.m_localPlayer.TeleportTo(pos, Quaternion.identity, true);
                    return true;
                },
                adminOnly: true);
            new NorseCommand(
                "validate_rp_items",
                "List equipment item names that do not exist in ObjectDB.",
                args =>
                {
                    if (ObjectDB.instance == null || _database == null)
                    {
                        _log.LogWarning("ObjectDB or database not ready.");
                        return true;
                    }

                    HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (EquipmentSetDefinition set in _database.equipmentSets.Values)
                    {
                        if (set == null) continue;
                        if (set.weapons != null) foreach (string n in set.weapons) names.Add(n);
                        if (set.armor != null) foreach (string n in set.armor) names.Add(n);
                        if (set.tools != null) foreach (string n in set.tools) names.Add(n);
                    }

                    int missing = 0;
                    foreach (string name in names)
                    {
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (ObjectDB.instance.GetItemPrefab(name) == null)
                        {
                            _log.LogWarning($"validate_rp_items: MISSING '{name}'");
                            missing++;
                        }
                    }

                    _log.LogInfo($"validate_rp_items: {names.Count} items checked, {missing} missing.");
                    if (Player.m_localPlayer != null)
                        Player.m_localPlayer.Message(
                            MessageHud.MessageType.TopLeft,
                            $"Items: {missing} missing (see log)");
                    return true;
                },
                adminOnly: true);
            new NorseCommand(
                "rp_scan",
                "Bake real positions: rp_scan <Clan|NPC-ID|all> [dry] (stand inside the city)",
                args => { NpcPlacement.ScanCommand(args.Args); return true; },
                adminOnly: true);
            new NorseCommand(
                "rp_pop",
                "Population: rp_pop status|on|off|tick|spawn <Clan> <N>|clear [Clan|all]",
                args => { PopulationManager.PopCommand(args.Args); return true; },
                adminOnly: true);
            new NorseCommand(
                "rp_groups",
                "Groups: rp_groups list | rp_groups disband [Clan|all]",
                args => { PopulationManager.GroupsCommand(args.Args); return true; },
                adminOnly: true);
            new NorseCommand(
                "rp_audit",
                "Server audit of RP NPCs: missing, duplicated, orphaned, non-persistent. 'rp_audit fix' repairs.",
                args => { NpcLifecycle.AuditCommand(args.Args); return true; },
                adminOnly: true);
            new NorseCommand(
                "rp_check",
                "Check loaded NPCs in the world: underground, in water, stuck, floating, drifted. rp_check [Clan|NPC-ID|all] [fix]",
                args => { NpcPlacement.CheckCommand(args.Args); return true; },
                adminOnly: true);
           new NorseCommand(
                "rp_relay",
                "Show the progress of the two relay corridors toward Gruskuppel and Myrkur.",
                args => 
                {DialogueStages.RelayCommand();
                    return true; 
                },
                adminOnly: true);      
            new NorseCommand(
                "rp_career",
                "Choose your allegiance (once): rp_career renegat|azures|douze|horde|antagoniste  (admin: rp_career force <name>)",
                args => { ReputationManager.CareerCommand(args.Args); return true; },
                adminOnly: false);
            new NorseCommand(
                "rp_calendar",
                "Show this month's objectives for your career (360-day campaign).",
                args => { ReputationManager.CalendarCommand(); return true; },
                adminOnly: false);
            new NorseCommand(
                "rp_rep",
                "Reputation: status | month | add|set <Clan|Faction> <pts> [career] | grudge <NPC-ID> [career] | forgive <NPC-ID|all> [career]",
                args => { ReputationManager.RepCommand(args.Args); return true; },
                adminOnly: true);
            new NorseCommand(
                "rp_journal",
                "Show the clues you have heard and your standing with each clan.",
                args => { NpcTalk.JournalCommand(); return true; },
                adminOnly: false);
            new NorseCommand(
                "rp_flag",
                "Story flags (world-wide): rp_flag list | rp_flag set <flag> | rp_flag clear <flag>",
                args => { DialogueStages.FlagCommand(args.Args); return true; },
                adminOnly: true);
        }
        private void ShowNpcInfo(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _log.LogInfo("Usage: rp_npc_info NPC-001");
                return;
            }
            NpcDefinition npc;
            if (!_npcs.TryGetValue(id, out npc))
            {
                _log.LogWarning($"Unknown NPC ID: {id}");
                return;
            }
            ZDO zdo = FindNpcZdo(id);
            _log.LogInfo(
             $"[{id}] name='{npc.identity.name}', clan='{npc.clan}', " +
            $"role='{npc.role}', prefab='{npc.prefab}', " +
            $"worldZdo={(zdo != null ? "FOUND" : "NOT FOUND")}");
        }
        // ---------------------------------------------------------------------
        // JSON DATA CONTRACTS
        // ---------------------------------------------------------------------
        [Serializable]
        public sealed class NpcDatabase
        {
            public int schemaVersion;
            public DatabaseSettings settings = new DatabaseSettings();
            public WorldDefinition world = new WorldDefinition();
            public PopulationSettings population = new PopulationSettings();
            [JsonExtensionData] public IDictionary<string, JToken> extra;   // lore, scenarioStack...
            public Dictionary<string, CityZone> cityZones = new Dictionary<string, CityZone>();
            public GroupBehavior groupBehavior = new GroupBehavior();
            public ReputationSettings reputation = new ReputationSettings();
            public Dictionary<string, CareerDefinition> careers = new Dictionary<string, CareerDefinition>();
            public Dictionary<string, FactionDefinition> factions =
                new Dictionary<string, FactionDefinition>();
            public Dictionary<string, ClanDefinition> clans =
                new Dictionary<string, ClanDefinition>();
            public Dictionary<string, EquipmentSetDefinition> equipmentSets =
                new Dictionary<string, EquipmentSetDefinition>();
            public Dictionary<string, DialogueSetDefinition> dialogueSets =
                new Dictionary<string, DialogueSetDefinition>();
            public Dictionary<string, NpcDefinition> npcs =
                new Dictionary<string, NpcDefinition>();
        }
        [Serializable]
        public sealed class DatabaseSettings
        {
            public bool enabled = true;
            public bool spawnOnWorldLoad;
            public bool preventDuplicates = true;
            public bool respawnIfDead = true;       // single switch for respawn (reputation no longer owns it)
            public float respawnDelaySeconds = 1800f; // world time
            public bool debug;
            public bool snapToGround = true;
            public float spawnHeightOffset = 0.5f;
            public string defaultPlayerFaction;
            public PlacementSettings placement = new PlacementSettings();
        }
        [Serializable]
        public sealed class FactionDefinition
        {
            public bool isFriendly = true;
            public string aggressiveVariant;
        }
        [Serializable]
        public sealed class ClanDefinition
        {
            public string faction;
            public CityPosition cityPosition;
            public string defaultDialogueSet;
        }
        [Serializable]
        public sealed class CityPosition
        {
            public float x;
            public float z;
        }
        [Serializable]
        public sealed class WorldDefinition
        {
            public Dictionary<string, CityDefinition> cities =
                new Dictionary<string, CityDefinition>();
        }
        [Serializable]
        public sealed class CityDefinition
        {
            public float x;
            public float z;
            public string faction;
        }
        [Serializable]
        public sealed class EquipmentSetDefinition
        {
            public string mode = "fixed";
            public string livingNorsemenProfession;
            public List<string> weapons = new List<string>();
            public List<string> armor = new List<string>();
            public List<string> tools = new List<string>();
        }
        [Serializable]
        public sealed class DialogueSetDefinition
        {
            public List<string> Greets = new List<string>();
            public List<string> Farewells = new List<string>();
            public List<string> Generic = new List<string>();
            public List<string> PlayerBase = new List<string>();
            public List<string> Damaged = new List<string>();
            public List<string> Thieved = new List<string>();
            public List<string> Puke = new List<string>();
            public List<string> Eat = new List<string>();
            public List<DialogueStage> stages = new List<DialogueStage>();
        }
        [Serializable]
        public sealed class NpcDefinition
        {
            public IdentityDefinition identity = new IdentityDefinition();
            public string prefab;
            public string clan;
            public string faction;
            public string repFaction;   // reputation faction: clan faction, Antagonistes or Vagabonds
            public string role;
            public LivingNorsemenDefinition livingNorsemen =
                new LivingNorsemenDefinition();
            public string equipmentSet;
            [JsonExtensionData] public IDictionary<string, JToken> extra;   // scenario card (MJ data), preserved on save
            public LocationsDefinition locations =
                new LocationsDefinition();
            public BehaviorDefinition behavior =
                new BehaviorDefinition();
            public DialogueReference dialogue =
                new DialogueReference();
        }
        [Serializable]
        public sealed class IdentityDefinition
        {
            public string name;
            public string gender;
        }
        [Serializable]
        public sealed class LivingNorsemenDefinition
        {
            public string profession;
        }
        [Serializable]
        public sealed class LocationsDefinition
        {
            public SpawnPosition spawn;
            public SpawnPosition home;
            public SpawnPosition work;
            public SpawnPosition patrolPointB;
        }
        [Serializable]
        public sealed class SpawnPosition
        {
            public bool enabled = true;
            public float x;
            public float y;
            public float z;
            public float rotationY;
            public bool baked;   // true = position verified on the real world by rp_scan
        }
        [Serializable]
        public sealed class BehaviorDefinition
        {
            public bool isStatic;
            public bool aggressive;
        }
        [Serializable]
        public sealed class DialogueReference
        {
            public string set;
        }
    }
    // =====================================================================
    // COMPILE-SAFE ACCESSORS (internal Valheim members reached through reflection, resolved once)
    // =====================================================================
    internal static class ZdoAccess
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo ObjectsById = typeof(ZDOMan).GetField("m_objectsByID", Any);
        private static readonly FieldInfo PrefabField = typeof(ZDO).GetField("m_prefab", Any);
        private static readonly FieldInfo RotationField = typeof(ZDO).GetField("m_rotation", Any);
        private static bool _warned;

        private static void Warn(string what)
        {
            if (_warned) return;
            _warned = true;
            ManagerNPCRP.Instance?.Log.LogError("Valheim field not found by reflection: " + what +
                                                " (game version changed?) - spawn/audit features degraded.");
        }

        /// <summary>Snapshot of every ZDO in the world (empty if the field cannot be reached).</summary>
        internal static List<ZDO> All()
        {
            List<ZDO> list = new List<ZDO>();
            if (ZDOMan.instance == null) return list;
            if (ObjectsById == null) { Warn("ZDOMan.m_objectsByID"); return list; }

            System.Collections.IDictionary dict = ObjectsById.GetValue(ZDOMan.instance) as System.Collections.IDictionary;
            if (dict == null) return list;

            foreach (object o in dict.Values)
            {
                ZDO z = o as ZDO;
                if (z != null) list.Add(z);
            }
            return list;
        }

        internal static void SetPrefab(ZDO zdo, int hash)
        {
            if (PrefabField == null) { Warn("ZDO.m_prefab"); return; }
            PrefabField.SetValue(zdo, hash);
        }

        internal static void SetRotation(ZDO zdo, Vector3 euler)
        {
            if (RotationField == null) { Warn("ZDO.m_rotation"); return; }
            // m_rotation is a Vector3 in current versions; fall back to a Quaternion if it changes.
            if (RotationField.FieldType == typeof(Vector3)) RotationField.SetValue(zdo, euler);
            else if (RotationField.FieldType == typeof(Quaternion)) RotationField.SetValue(zdo, Quaternion.Euler(euler));
        }
    }
    internal static class RpcAccess
    {
        private static readonly MethodInfo ServerPeer = typeof(ZRoutedRpc).GetMethod("GetServerPeerID",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>Server peer id; when unreachable, broadcasts (every handler checks IsServer() itself).</summary>
        internal static long ServerPeerId()
        {
            if (ZRoutedRpc.instance != null && ServerPeer != null)
            {
                try { return (long)ServerPeer.Invoke(ZRoutedRpc.instance, null); }
                catch { }
            }
            return ZRoutedRpc.Everybody;
        }
    }
    // =====================================================================
    // LIFECYCLE: respawn, reconciliation, audit  (single owner of respawnIfDead)
    // ====================================================================
    public sealed class LifeWorld
    {
        public Dictionary<string, double> deadSince = new Dictionary<string, double>();
        public bool everSpawned;      // spawn_rp_npcs ran at least once in this world
    }
    public sealed class LifeState
    {
        public Dictionary<string, LifeWorld> worlds = new Dictionary<string, LifeWorld>();
    }
    internal static class NpcLifecycle
    {
        private const string RpIdKey = "NorsemanNPC_RP_ID";
        private static bool _running;
        private static LifeState _state;
        private static float _bootTime;
        private static float _nextReconcile;
        private static string StatePath
        {
            get { return Path.Combine(Paths.ConfigPath, "Norseman-NPC-RP", "lifecycle_state.json"); }
        }
        private static LifeWorld Ws()
        {
            if (_state == null)
            {
                try
                {
                    _state = File.Exists(StatePath)
                        ? JsonConvert.DeserializeObject<LifeState>(File.ReadAllText(StatePath)) : null;
                }
                catch { _state = null; }
                if (_state == null) _state = new LifeState();
            }
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "unknown";
            LifeWorld ws;
            if (!_state.worlds.TryGetValue(world, out ws)) _state.worlds[world] = ws = new LifeWorld();
            return ws;
        }
        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(StatePath, JsonConvert.SerializeObject(_state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                ManagerNPCRP.Instance?.Log.LogWarning("Lifecycle state not saved: " + ex.Message);
            }
        }
        internal static void RecordDeath(string id)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            Ws().deadSince[id] = ZNet.instance.GetTimeSeconds();
            Save();
        }
        internal static bool IsAwaitingRespawn(string id)
        {
            return ZNet.instance != null && Ws().deadSince.ContainsKey(id);
        }
        internal static void ClearDead(string id)
        {
            if (ZNet.instance != null && Ws().deadSince.Remove(id)) Save();
        }
        internal static void MarkEverSpawned()
        {
            if (ZNet.instance == null) return;
            LifeWorld ws = Ws();
            if (!ws.everSpawned) { ws.everSpawned = true; Save(); }
        }
        internal static List<string> DeadList()
        {
            return ZNet.instance != null ? Ws().deadSince.Keys.ToList() : new List<string>();
        }
        internal static IEnumerator Run(ManagerNPCRP plugin)
        {
            if (_running) yield break;
            _running = true;
            _bootTime = Time.realtimeSinceStartup;
            while (true)
            {
                yield return new WaitForSeconds(30f);

                ManagerNPCRP.NpcDatabase db = plugin.Database;
                if (db == null || !db.settings.enabled || !db.settings.respawnIfDead) continue;
                if (ZNet.instance == null || !ZNet.instance.IsServer() ||
                    ZDOMan.instance == null || ZNetScene.instance == null) continue;

                try { Tick(plugin); }
                catch (Exception ex) { plugin.Log.LogError("Lifecycle tick failed: " + ex); }
            }
        }
        private static void Tick(ManagerNPCRP plugin)
        {
            ManagerNPCRP.NpcDatabase db = plugin.Database;
            LifeWorld ws = Ws();
            double now = ZNet.instance.GetTimeSeconds();
            float delay = db.settings.respawnDelaySeconds;
            // World restored from an older backup: never wait longer than one delay.
            foreach (string id in ws.deadSince.Keys.ToList())
                if (ws.deadSince[id] > now) ws.deadSince[id] = now;
            // Reconcile: an NPC that should exist, is absent, and whose death nobody recorded
            // (owner disconnected mid-fight, crash...) is scheduled like a normal death.
            // Only after a first spawn_rp_npcs (or spawnOnWorldLoad), and not right after boot.
            if ((ws.everSpawned || db.settings.spawnOnWorldLoad) &&
                Time.realtimeSinceStartup - _bootTime > 180f && Time.realtimeSinceStartup >= _nextReconcile)
            {
                _nextReconcile = Time.realtimeSinceStartup + 300f;
                Dictionary<string, List<ZDO>> byId = plugin.CollectZdosById();
                foreach (KeyValuePair<string, ManagerNPCRP.NpcDefinition> kv in plugin.Npcs)
                {
                    ManagerNPCRP.SpawnPosition sp = kv.Value.locations != null ? kv.Value.locations.spawn : null;
                    if (sp == null || !sp.enabled) continue;
                    if (byId.ContainsKey(kv.Key) || ws.deadSince.ContainsKey(kv.Key)) continue;

                    ws.deadSince[kv.Key] = now;
                    plugin.Log.LogInfo($"[{kv.Key}] missing from the world: respawn scheduled in {delay:0}s.");
                }
            }
            foreach (string id in ws.deadSince.Keys.ToList())
            {
                if (now - ws.deadSince[id] < delay) continue;
                if (plugin.RespawnNamed(id))
                {
                    ws.deadSince.Remove(id);
                    plugin.Log.LogInfo("Respawned " + id);
                }
            }
            Save();
        }
        // ------------------------------------------------------------ rp_audit [fix]
        internal static void AuditCommand(string[] args)
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null || plugin.Database == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
            {
                plugin.Log.LogInfo("rp_audit must run on the host/server.");
                return;
            }
            bool fix = args.Any(a => a.Equals("fix", StringComparison.OrdinalIgnoreCase));
            Dictionary<string, List<ZDO>> byId = plugin.CollectZdosById();
            List<string> lines = new List<string>();
            int expected = 0, present = 0, missing = 0, duplicates = 0, orphans = 0, notPersistent = 0,
                generated = 0, notPlaced = 0, gaveUp = 0, removed = 0;
            foreach (KeyValuePair<string, ManagerNPCRP.NpcDefinition> kv in plugin.Npcs)
            {
                ManagerNPCRP.SpawnPosition sp = kv.Value.locations != null ? kv.Value.locations.spawn : null;
                if (sp == null || !sp.enabled) continue;
                expected++;
                List<ZDO> found;
                if (!byId.TryGetValue(kv.Key, out found) || found.Count == 0)
                {
                    missing++;
                    lines.Add($"MISSING   {kv.Key} {kv.Value.identity.name}" +
                              (IsAwaitingRespawn(kv.Key) ? " (respawn scheduled)" : " (not scheduled: run spawn_rp_npcs)"));
                    continue;
                }
                present++;
                if (found.Count > 1)
                {
                    duplicates++;
                    lines.Add($"DUPLICATE {kv.Key} {kv.Value.identity.name}: {found.Count} ZDOs");
                    if (fix)
                    {
                        // keep one that is already placed, otherwise the first
                        ZDO keep = found.FirstOrDefault(z => z.GetBool(NpcPlacement.PlacedKey, false)) ?? found[0];
                        foreach (ZDO z in found)
                            if (z != keep) { ZDOMan.instance.DestroyZDO(z); removed++; }
                    }
                }
            }
            foreach (KeyValuePair<string, List<ZDO>> kv in byId)
            {
                bool isGen = kv.Key.StartsWith("GEN-", StringComparison.Ordinal);
                if (isGen) generated += kv.Value.Count;
                else if (!plugin.Npcs.ContainsKey(kv.Key))
                {
                    orphans += kv.Value.Count;
                    lines.Add($"ORPHAN    {kv.Key}: not in the database ({kv.Value.Count} ZDO)");
                }
                foreach (ZDO z in kv.Value)
                {
                    if (!z.Persistent)
                    {
                        notPersistent++;
                        if (fix) z.Persistent = true;
                    }
                    if (!z.GetBool(NpcPlacement.PlacedKey, false)) notPlaced++;
                    if (z.GetBool(NpcPlacement.GaveUpKey, false))
                    {
                        gaveUp++;
                        lines.Add($"PLACEMENT {kv.Key}: gave up (see placement_report.txt)");
                    }
                }
            }
            List<string> dead = DeadList();
            int loaded = Character.GetAllCharacters().Count(c => c is Viking);
            string summary = $"rp_audit: {expected} expected, {present} present, {missing} missing, " +
                             $"{duplicates} duplicated, {orphans} orphan ZDO(s), {generated} generated citizens, " +
                             $"{notPersistent} non-persistent, {notPlaced} not placed yet, {gaveUp} placement gave up, " +
                             $"{dead.Count} awaiting respawn, {loaded} loaded in memory" +
                             (fix ? $" | FIX: {removed} extra ZDO(s) removed, persistence forced." : ".");
            lines.Insert(0, summary);
            foreach (string l in lines.Take(80)) plugin.Log.LogInfo(l);
            if (lines.Count > 80) plugin.Log.LogInfo($"... {lines.Count - 80} more line(s) in npc_audit.txt");
            try
            {
                File.WriteAllText(Path.Combine(Paths.ConfigPath, "Norseman-NPC-RP", "npc_audit.txt"),
                                  string.Join("\n", lines.ToArray()));
            }
            catch { }
            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft,
                    $"Audit: {missing} missing, {duplicates} dup, {notPersistent} non-persist (see log)");
        }
    }
    // =====================================================================
    // REPUTATION (company-wide, per clan) + KILLS / GRUDGES + TALK ENGINE
    // =====================================================================
    [Serializable]
    public sealed class RepReward
    {
        public string flag;
        public string clan;       // clan-level reward (also feeds its faction)
        public string faction;    // faction-level reward when no clan
        public string career;     // null = every active career
        public int points;
    }
    [Serializable]
    public sealed class ReputationTexts
    {
        public List<string> grudge = new List<string>();
        public List<string> hostile = new List<string>();
        public List<List<string>> reserved = new List<List<string>>();
    }
    [Serializable]
    public sealed class CareerDefinition
    {
        public string name;
        public string faction;
        public string description;
        public List<string> hubClans = new List<string>();
        public Dictionary<string, float> monthlyDrift = new Dictionary<string, float>();
        [JsonExtensionData] public IDictionary<string, JToken> extra;   // calendar (12 months)
    }
    [Serializable]
    public sealed class ReputationSettings
    {
        public string _note;
        public bool enabled = true;
        public float tickSeconds = 15f;
        public float cityRadius = 90f;
        public int minPlayersForNight = 1;
        public int pointsPerNight = 4;
        public int nightPointsCap = 48;
        public int tier1 = 12;
        public int tier2 = 32;
        public int tier3 = 64;
        public int hostileBelow = -20;
        public int killPenaltyCitizen = 8;
        public int killPenaltyNamed = 30;
        public int killPenaltyPillar = 45;
        public int monthDays = 30;
        public List<string> repFactions = new List<string> { "DouzeCouronnes", "CamaradesAzures", "HordeSolaire", "Vagabonds", "Antagonistes" };
        public float factionShare = 0.4f;          // share of a quest/kill clan delta that also moves the faction
        public Dictionary<string, float> factionShareBy = new Dictionary<string, float>();   // per faction (Douze has 12 clans, the others 4)
        public float nightFactionShare = 0.06f;    // nights barely move the faction: time in town builds LOCAL trust
        public int factionHostileBelow = -25;      // faction hostile => its Guards attack
        public int factionTier1 = 10;
        public int factionTier2 = 28;
        public int factionTier3 = 55;
        public float roadNightFactionPoints = 0.12f; // night spent outside any city: Vagabonds
        public int maxPoints = 100;
        public Dictionary<string, Dictionary<string, float>> spill = new Dictionary<string, Dictionary<string, float>>();
        public List<string> tierNames = new List<string> { "Inconnus", "Familiers", "De confiance", "Intimes" };
        public List<RepReward> rewards = new List<RepReward>();
        public ReputationTexts texts = new ReputationTexts();
    }
    public sealed class Ledger
    {
        public Dictionary<string, int> clanPoints = new Dictionary<string, int>();
        public Dictionary<string, int> clanNights = new Dictionary<string, int>();
        public Dictionary<string, int> clanNightPoints = new Dictionary<string, int>();
        public Dictionary<string, float> factionPoints = new Dictionary<string, float>();
    }
    public sealed class RepWorld
    {
        public Dictionary<string, Ledger> ledgers = new Dictionary<string, Ledger>();     // one per career
        public Dictionary<string, string> playerCareer = new Dictionary<string, string>();
        public List<string> awarded = new List<string>();
        public Dictionary<string, string> dusk = new Dictionary<string, string>();
        public bool wasNight;
        public int lastMonth = -1;
        public List<string> history = new List<string>();
    }
    public sealed class RepState
    {
        public Dictionary<string, RepWorld> worlds = new Dictionary<string, RepWorld>();
    }
    internal static class ReputationManager
    {
        private const string RpIdKey = "NorsemanNPC_RP_ID";
        private const string ClanKey = "NorsemanNPC_Clan";
        private const string CareerKey = "RP_Career";
        internal const string DefaultCareer = "renegat";
        private static bool _running;
        private static bool _announced;
        private static float _nextClientTick;
        private static float _lastPrompt = -999f;
        private static RepState _state;
        private static readonly FieldInfo LastHit = typeof(Character).GetField("m_lastHit",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly Dictionary<string, float> LastRefusal = new Dictionary<string, float>();

        private static ManagerNPCRP.NpcDatabase Db
        {
            get { return ManagerNPCRP.Instance != null ? ManagerNPCRP.Instance.Database : null; }
        }

        private static ReputationSettings Cfg { get { return Db != null ? Db.reputation : null; } }

        // ============================================================ careers + scoped flags

        internal static string LocalCareer()
        {
            Player lp = Player.m_localPlayer;
            string c;
            if (lp != null && lp.m_customData.TryGetValue(CareerKey, out c) && !string.IsNullOrEmpty(c)) return c;
            return DefaultCareer;
        }

        private static bool HasChosenCareer()
        {
            Player lp = Player.m_localPlayer;
            string c;
            return lp != null && lp.m_customData.TryGetValue(CareerKey, out c) && !string.IsNullOrEmpty(c);
        }

        internal static bool IsScoped(string f)
        {
            return f.StartsWith("rep_", StringComparison.Ordinal) || f.StartsWith("rephostile_", StringComparison.Ordinal) ||
                   f.StartsWith("repf_", StringComparison.Ordinal) || f.StartsWith("repfhostile_", StringComparison.Ordinal) ||
                   f.StartsWith("grudge_", StringComparison.Ordinal);
        }

        internal static string Scope(string f)
        {
            int i = f.IndexOf('_');
            return f.Substring(0, i + 1) + LocalCareer() + "_" + f.Substring(i + 1);
        }

        private static void RawSet(string flag, bool on)
        {
            if (ZoneSystem.instance == null) return;
            bool cur = DialogueStages.RawOn(flag);
            if (on && !cur) ZoneSystem.instance.SetGlobalKey("rp_" + flag);
            else if (!on && cur) ZoneSystem.instance.RemoveGlobalKey("rp_" + flag);
        }

        private static string CareerOf(RepWorld ws, string playerName)
        {
            string c;
            return ws.playerCareer.TryGetValue(playerName ?? "", out c) && !string.IsNullOrEmpty(c) ? c : DefaultCareer;
        }

        private static string RepFactionOf(ManagerNPCRP.NpcDefinition npc)
        {
            return !string.IsNullOrEmpty(npc.repFaction) ? npc.repFaction : npc.faction;
        }

        private static string ClanFaction(ManagerNPCRP.NpcDatabase db, string clan)
        {
            if (clan == "Itinerants") return "Vagabonds";
            ManagerNPCRP.ClanDefinition cd;
            return db.clans.TryGetValue(clan ?? "", out cd) && cd != null ? cd.faction : "";
        }

        // ============================================================ tiers (client side, from flags)

        internal static int TierFromFlags(string clan)
        {
            for (int t = 3; t >= 1; t--)
                if (DialogueStages.FlagOn("rep_" + clan + "_" + t)) return t;
            return 0;
        }

        internal static int FactionTierFromFlags(string faction)
        {
            for (int t = 3; t >= 1; t--)
                if (DialogueStages.FlagOn("repf_" + faction + "_" + t)) return t;
            return 0;
        }

        /// <summary>How open this NPC is to the local player. Allied factions open doors one tier faster.</summary>
        internal static int TierFor(ManagerNPCRP.NpcDefinition npc)
        {
            string rf = RepFactionOf(npc);
            int factionTier = FactionTierFromFlags(rf ?? "");
            if (rf == "Antagonistes" || rf == "Vagabonds" || Db == null || !Db.cityZones.ContainsKey(npc.clan ?? ""))
                return factionTier;
            return Mathf.Max(TierFromFlags(npc.clan), Mathf.Max(0, factionTier - 1));
        }

        // ============================================================ state (server, per world)

        private static string StatePath
        {
            get { return Path.Combine(Paths.ConfigPath, "Norseman-NPC-RP", "reputation_state.json"); }
        }

        private static RepWorld Ws()
        {
            if (_state == null)
            {
                try
                {
                    _state = File.Exists(StatePath)
                        ? JsonConvert.DeserializeObject<RepState>(File.ReadAllText(StatePath)) : null;
                }
                catch { _state = null; }
                if (_state == null) _state = new RepState();
            }

            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "unknown";
            RepWorld ws;
            if (!_state.worlds.TryGetValue(world, out ws)) _state.worlds[world] = ws = new RepWorld();
            return ws;
        }

        private static Ledger Led(RepWorld ws, string career)
        {
            Ledger l;
            if (!ws.ledgers.TryGetValue(career, out l)) ws.ledgers[career] = l = new Ledger();
            return l;
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(StatePath, JsonConvert.SerializeObject(_state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                ManagerNPCRP.Instance?.Log.LogWarning("Reputation state not saved: " + ex.Message);
            }
        }

        // ============================================================ points

        private static int TierOf(int pts, int t1, int t2, int t3) { return pts >= t3 ? 3 : pts >= t2 ? 2 : pts >= t1 ? 1 : 0; }

        private static string TierName(ReputationSettings r, int tier)
        {
            return tier >= 0 && tier < r.tierNames.Count ? r.tierNames[tier] : tier.ToString();
        }

        private static void Bump(Ledger l, string faction, float delta, int max)
        {
            float v; l.factionPoints.TryGetValue(faction, out v);
            l.factionPoints[faction] = Mathf.Clamp(v + delta, -max, max);
        }

        /// <summary>A faction delta also moves the other factions through the spill matrix (rivals lose what allies gain).</summary>
        private static void AddFaction(ManagerNPCRP plugin, string career, string faction, float delta)
        {
            if (string.IsNullOrEmpty(faction) || Mathf.Approximately(delta, 0f)) return;
            ReputationSettings r = plugin.Database.reputation;
            Ledger l = Led(Ws(), career);
            Bump(l, faction, delta, r.maxPoints);

            Dictionary<string, float> sp;
            if (r.spill.TryGetValue(faction, out sp))
                foreach (KeyValuePair<string, float> kv in sp)
                    Bump(l, kv.Key, delta * kv.Value, r.maxPoints);
        }

        private static void AddClan(ManagerNPCRP plugin, string career, string clan, int delta, float share = -1f)
        {
            if (string.IsNullOrEmpty(clan) || delta == 0) return;
            ReputationSettings r = plugin.Database.reputation;
            Ledger l = Led(Ws(), career);
            int v; l.clanPoints.TryGetValue(clan, out v);
            l.clanPoints[clan] = Mathf.Clamp(v + delta, -r.maxPoints, r.maxPoints);
            string faction = ClanFaction(plugin.Database, clan);
            float use = share;
            if (use < 0f)
            {
                float by;
                use = r.factionShareBy.TryGetValue(faction, out by) ? by : r.factionShare;
            }
            AddFaction(plugin, career, faction, delta * use);
        }

        // ============================================================ flags published for the clients

        private static void SyncClan(ManagerNPCRP plugin, string career, string clan, bool notify)
        {
            ReputationSettings r = plugin.Database.reputation;
            int pts; Led(Ws(), career).clanPoints.TryGetValue(clan, out pts);
            bool hostile = pts <= r.hostileBelow;
            int tier = hostile ? 0 : TierOf(pts, r.tier1, r.tier2, r.tier3);

            string baseKey = "rep_" + career + "_" + clan + "_";
            int before = 0;
            for (int t = 3; t >= 1; t--) if (DialogueStages.RawOn(baseKey + t)) { before = t; break; }
            bool hostileBefore = DialogueStages.RawOn("rephostile_" + career + "_" + clan);

            for (int n = 1; n <= 3; n++) RawSet(baseKey + n, tier >= n);
            RawSet("rephostile_" + career + "_" + clan, hostile);

            if (notify && (tier != before || hostile != hostileBefore))
                Notice(career, hostile ? clan + " vous est désormais hostile."
                                       : "Réputation à " + clan + " : " + TierName(r, tier) + ".");
        }

        private static void SyncFaction(ManagerNPCRP plugin, string career, string faction, bool notify)
        {
            ReputationSettings r = plugin.Database.reputation;
            float f; Led(Ws(), career).factionPoints.TryGetValue(faction, out f);
            int pts = Mathf.RoundToInt(f);
            bool hostile = pts <= r.factionHostileBelow;
            int tier = hostile ? 0 : TierOf(pts, r.factionTier1, r.factionTier2, r.factionTier3);

            string baseKey = "repf_" + career + "_" + faction + "_";
            int before = 0;
            for (int t = 3; t >= 1; t--) if (DialogueStages.RawOn(baseKey + t)) { before = t; break; }
            bool hostileBefore = DialogueStages.RawOn("repfhostile_" + career + "_" + faction);

            for (int n = 1; n <= 3; n++) RawSet(baseKey + n, tier >= n);
            RawSet("repfhostile_" + career + "_" + faction, hostile);

            if (notify && hostile != hostileBefore)
                Notice(career, hostile ? faction + " vous est devenue hostile : ses gardes vous attaqueront."
                                       : faction + " n'est plus hostile.");
            else if (notify && tier != before)
                Notice(career, "Standing avec " + faction + " : " + TierName(r, tier) + ".");
        }

        private static void SyncAll(ManagerNPCRP plugin, string career, bool notify)
        {
            foreach (string clan in plugin.Database.cityZones.Keys) SyncClan(plugin, career, clan, notify);
            foreach (string f in plugin.Database.reputation.repFactions) SyncFaction(plugin, career, f, notify);
        }

        // ============================================================ server loop

        internal static IEnumerator Run(ManagerNPCRP plugin)
        {
            if (_running) yield break;
            _running = true;

            while (true)
            {
                ReputationSettings r = Cfg;
                yield return new WaitForSeconds(Mathf.Max(5f, r != null ? r.tickSeconds : 15f));

                ManagerNPCRP.NpcDatabase db = plugin.Database;
                if (db == null || db.reputation == null || !db.reputation.enabled) continue;
                if (ZNet.instance == null || !ZNet.instance.IsServer() ||
                    ZoneSystem.instance == null || EnvMan.instance == null) continue;

                try { ServerTick(plugin); }
                catch (Exception ex) { plugin.Log.LogError("Reputation tick failed: " + ex); }
            }
        }

        private static List<KeyValuePair<string, Vector3>> Players()
        {
            List<KeyValuePair<string, Vector3>> list = new List<KeyValuePair<string, Vector3>>();
            if (Player.m_localPlayer != null)
                list.Add(new KeyValuePair<string, Vector3>(Player.m_localPlayer.GetPlayerName(),
                                                            Player.m_localPlayer.transform.position));
            if (ZNet.instance != null)
                foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                    list.Add(new KeyValuePair<string, Vector3>(peer.m_playerName, peer.m_refPos));
            return list;
        }

        private static string ZoneClanAt(ManagerNPCRP.NpcDatabase db, Vector3 pos)
        {
            string best = null;
            float bestD = float.MaxValue;
            foreach (KeyValuePair<string, CityZone> kv in db.cityZones)
            {
                float dx = pos.x - kv.Value.x, dz = pos.z - kv.Value.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d <= db.reputation.cityRadius && d < bestD) { best = kv.Key; bestD = d; }
            }
            return best;
        }

        private static void ServerTick(ManagerNPCRP plugin)
        {
            ManagerNPCRP.NpcDatabase db = plugin.Database;
            ReputationSettings r = db.reputation;
            RepWorld ws = Ws();
            bool night = EnvMan.IsNight();
            List<KeyValuePair<string, Vector3>> players = Players();

            HashSet<string> active = new HashSet<string>();
            foreach (KeyValuePair<string, Vector3> p in players) active.Add(CareerOf(ws, p.Key));

            // ---- nights: same city at dusk AND dawn = a night slept in town; outside any city = a night on the road
            if (night && !ws.wasNight)
            {
                ws.dusk.Clear();
                foreach (KeyValuePair<string, Vector3> p in players) ws.dusk[p.Key] = ZoneClanAt(db, p.Value) ?? "-";
            }
            else if (!night && ws.wasNight)
            {
                Dictionary<string, int> counts = new Dictionary<string, int>();
                foreach (KeyValuePair<string, Vector3> p in players)
                {
                    string now = ZoneClanAt(db, p.Value) ?? "-";
                    string at;
                    if (!ws.dusk.TryGetValue(p.Key, out at) || at != now) continue;

                    string career = CareerOf(ws, p.Key);
                    if (now == "-") { AddFaction(plugin, career, "Vagabonds", r.roadNightFactionPoints); continue; }

                    string key = career + "|" + now;
                    int n; counts.TryGetValue(key, out n); counts[key] = n + 1;
                }

                foreach (KeyValuePair<string, int> kv in counts)
                {
                    if (kv.Value < r.minPlayersForNight) continue;
                    string[] parts = kv.Key.Split('|');
                    Ledger l = Led(ws, parts[0]);

                    int nights; l.clanNights.TryGetValue(parts[1], out nights); l.clanNights[parts[1]] = nights + 1;
                    int used; l.clanNightPoints.TryGetValue(parts[1], out used);
                    int add = Mathf.Max(0, Mathf.Min(r.pointsPerNight, r.nightPointsCap - used));
                    l.clanNightPoints[parts[1]] = used + add;
                    AddClan(plugin, parts[0], parts[1], add, r.nightFactionShare);
                }

                ws.dusk.Clear();
            }

            ws.wasNight = night;

            // ---- quest progress pays every active career concerned (once)
            foreach (RepReward rw in r.rewards)
            {
                if (!DialogueStages.RawOn(rw.flag)) continue;
                foreach (string career in active)
                {
                    if (!string.IsNullOrEmpty(rw.career) && rw.career != career) continue;
                    string key = career + "|" + rw.flag + "|" + (rw.clan ?? rw.faction);
                    if (ws.awarded.Contains(key)) continue;
                    ws.awarded.Add(key);
                    if (!string.IsNullOrEmpty(rw.clan)) AddClan(plugin, career, rw.clan, rw.points);
                    else AddFaction(plugin, career, rw.faction, rw.points);
                }
            }

            // ---- every 30 in-game days: drift + new faction table
            int month = EnvMan.instance.GetDay() / Math.Max(1, r.monthDays);
            if (month < ws.lastMonth) ws.lastMonth = month;   // world restored from an older backup
            if (ws.lastMonth < 0) ws.lastMonth = month;
            else if (month > ws.lastMonth)
            {
                int guard = 0;
                while (ws.lastMonth < month && guard++ < 3)
                {
                    ws.lastMonth++;
                    MonthlyUpdate(plugin, ws, ws.lastMonth, active);
                }
                ws.lastMonth = month;
            }

            foreach (string career in active) SyncAll(plugin, career, true);

            Save();
        }

        private static string Summary(ManagerNPCRP plugin, string career)
        {
            ReputationSettings r = plugin.Database.reputation;
            Ledger l = Led(Ws(), career);
            List<string> bits = new List<string>();
            foreach (string f in r.repFactions)
            {
                float v; l.factionPoints.TryGetValue(f, out v);
                int pts = Mathf.RoundToInt(v);
                bits.Add(f + " " + pts + (pts <= r.factionHostileBelow ? " HOSTILE"
                         : " (" + TierName(r, TierOf(pts, r.factionTier1, r.factionTier2, r.factionTier3)) + ")"));
            }
            return string.Join(" | ", bits.ToArray());
        }

        private static void MonthlyUpdate(ManagerNPCRP plugin, RepWorld ws, int month, HashSet<string> active)
        {
            ReputationSettings r = plugin.Database.reputation;
            foreach (string career in active)
            {
                CareerDefinition cd;
                if (!plugin.Database.careers.TryGetValue(career, out cd)) continue;

                Ledger l = Led(ws, career);
                foreach (string f in r.repFactions)
                {
                    float drift;
                    if (cd.monthlyDrift.TryGetValue(f, out drift)) Bump(l, f, drift, r.maxPoints);
                }

                SyncAll(plugin, career, true);
                string line = "Mois " + month + " [" + career + "] " + Summary(plugin, career);
                ws.history.Add(line);
                if (ws.history.Count > 60) ws.history.RemoveAt(0);
                plugin.Log.LogInfo(line);
                Notice(career, "Table de réputation mise à jour (mois " + month + "). Voir rp_journal.");
            }
        }

        // ============================================================ notices + RPC

        private static void Notice(string career, string text)
        {
            if (ZRoutedRpc.instance != null)
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "RP_Notice", career, text);
        }

        internal static void GameStartPostfix()
        {
            _announced = false;
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.Register<string, string>("RP_Notice", RpcNotice);
            ZRoutedRpc.instance.Register<string, string>("RP_Career", RpcCareer);
            ZRoutedRpc.instance.Register<string, string, string>("RP_Death", RpcDeath);
        }

        private static void RpcNotice(long sender, string career, string text)
        {
            if (Player.m_localPlayer != null && LocalCareer() == career)
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, text);
        }

        // Server: a player told us which career he chose.
        private static void RpcCareer(long sender, string playerName, string career)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || Db == null || !Db.careers.ContainsKey(career)) return;
            Ws().playerCareer[playerName] = career;
            Save();
        }

        private static string ScenarioTier(ManagerNPCRP.NpcDefinition npc)
        {
            JToken sc;
            if (npc != null && npc.extra != null && npc.extra.TryGetValue("scenario", out sc) && sc["tier"] != null)
                return (string)sc["tier"];
            return "";
        }

        // Server only.
        private static void RpcDeath(long sender, string id, string clan, string killerName)
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null || plugin.Database == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            // Respawn bookkeeping is independent from the reputation system.
            if (!id.StartsWith("GEN-", StringComparison.Ordinal)) NpcLifecycle.RecordDeath(id);

            ReputationSettings r = plugin.Database.reputation;
            if (r == null || !r.enabled) return;

            RepWorld ws = Ws();
            bool byPlayer = !string.IsNullOrEmpty(killerName);
            string career = byPlayer ? CareerOf(ws, killerName) : null;

            if (id.StartsWith("GEN-", StringComparison.Ordinal))
            {
                if (byPlayer) { AddClan(plugin, career, clan, -r.killPenaltyCitizen); SyncAll(plugin, career, true); }
                Save();
                return;
            }

            if (byPlayer)
            {
                ManagerNPCRP.NpcDefinition npc;
                plugin.Npcs.TryGetValue(id, out npc);
                string tier = ScenarioTier(npc);
                int penalty = tier == "pillar" ? r.killPenaltyPillar : r.killPenaltyNamed;
                string rf = npc != null ? RepFactionOf(npc) : "";

                // Antagonists and vagabonds cost standing with THEIR faction (rivals are pleased through the spill);
                // everyone else costs the clan (and, through it, its faction).
                if (rf == "Antagonistes" || rf == "Vagabonds") AddFaction(plugin, career, rf, -penalty);
                else AddClan(plugin, career, clan, -penalty);

                RawSet("grudge_" + career + "_" + id, true);
                SyncAll(plugin, career, true);
                Notice(career, (npc != null && npc.identity != null ? npc.identity.name : id) +
                               " est mort de votre main. On s'en souviendra.");
            }

            Save();
        }

        internal static void CharacterOnDeathPostfix(Character __instance)
        {
            try
            {
                Viking v = __instance as Viking;
                if (v == null || ManagerNPCRP.Instance == null) return;

                ZNetView nview = v.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

                ZDO zdo = nview.GetZDO();
                string id = zdo.GetString(RpIdKey, "");
                if (id.Length == 0 || ZRoutedRpc.instance == null) return;

                string killer = "";
                HitData hit = LastHit != null ? LastHit.GetValue(__instance) as HitData : null;
                if (hit != null)
                {
                    Player p = hit.GetAttacker() as Player;
                    if (p != null) killer = p.GetPlayerName();
                }

                ZRoutedRpc.instance.InvokeRoutedRPC(RpcAccess.ServerPeerId(), "RP_Death",
                    id, zdo.GetString(ClanKey, ""), killer);
            }
            catch (Exception ex)
            {
                ManagerNPCRP.Instance?.Log.LogWarning("OnDeath hook failed: " + ex.Message);
            }
        }

        // ============================================================ client: career choice

        internal static void ClientTick()
        {
            if (Time.time < _nextClientTick) return;
            _nextClientTick = Time.time + 5f;

            Player lp = Player.m_localPlayer;
            if (lp == null || ZRoutedRpc.instance == null || Db == null) return;

            if (HasChosenCareer())
            {
                if (!_announced)
                {
                    _announced = true;
                    ZRoutedRpc.instance.InvokeRoutedRPC(RpcAccess.ServerPeerId(), "RP_Career",
                        lp.GetPlayerName(), LocalCareer());
                }
            }
            else if (Time.time - _lastPrompt > 120f)
            {
                _lastPrompt = Time.time;
                lp.Message(MessageHud.MessageType.Center,
                    "Choisissez votre allégeance : rp_career renegat | azures | douze | horde | antagoniste");
            }
        }

        internal static void CareerCommand(string[] args)
        {
            Player lp = Player.m_localPlayer;
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (lp == null || plugin == null || plugin.Database == null) return;
            ManagerNPCRP.NpcDatabase db = plugin.Database;

            string arg = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            if (arg == "list")
            {
                foreach (KeyValuePair<string, CareerDefinition> kv in db.careers)
                    plugin.Log.LogInfo("  " + kv.Key + " - " + kv.Value.name + " : " + kv.Value.description);
                lp.Message(MessageHud.MessageType.TopLeft, "Allégeance actuelle : " + (HasChosenCareer() ? LocalCareer() : "aucune"));
                return;
            }

            bool force = arg == "force";
            string choice = force ? (args.Length > 2 ? args[2].ToLowerInvariant() : "") : arg;
            if (!db.careers.ContainsKey(choice))
            {
                lp.Message(MessageHud.MessageType.TopLeft, "Allégeance inconnue : " + choice);
                return;
            }

            if (HasChosenCareer() && !force)
            {
                lp.Message(MessageHud.MessageType.TopLeft, "Allégeance déjà choisie : " + LocalCareer());
                return;
            }

            if (force && !(ZNet.instance != null && ZNet.instance.LocalPlayerIsAdminOrHost()))
            {
                lp.Message(MessageHud.MessageType.TopLeft, "Réservé aux administrateurs.");
                return;
            }

            lp.m_customData[CareerKey] = choice;
            _announced = false;
            lp.Message(MessageHud.MessageType.Center, "Allégeance : " + db.careers[choice].name);
        }

        internal static void CalendarCommand()
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null || plugin.Database == null || EnvMan.instance == null) return;
            ManagerNPCRP.NpcDatabase db = plugin.Database;

            CareerDefinition cd;
            JToken cal;
            if (!db.careers.TryGetValue(LocalCareer(), out cd) || cd.extra == null ||
                !cd.extra.TryGetValue("calendar", out cal) || !(cal is JArray)) return;

            JArray arr = (JArray)cal;
            int month = Mathf.Clamp(EnvMan.instance.GetDay() / Math.Max(1, db.reputation.monthDays) + 1, 1, arr.Count);
            JToken e = arr[month - 1];

            List<string> lines = new List<string>();
            lines.Add("=== " + cd.name + " - mois " + month + "/" + arr.Count + " : " + (string)e["title"] + " ===");
            lines.Add("  Fils : " + string.Join(", ", ((JArray)e["focus"]).Select(x => (string)x).ToArray()) +
                      " | Lieux : " + string.Join(", ", ((JArray)e["clans"]).Select(x => (string)x).ToArray()));
            foreach (JToken g in (JArray)e["goals"])
                lines.Add("  [" + (DialogueStages.RawOn((string)g) ? "x" : " ") + "] " + (string)g);
            if (e["note"] != null && !string.IsNullOrEmpty((string)e["note"])) lines.Add("  Note : " + (string)e["note"]);

            foreach (string l in lines)
            {
                if (Console.instance != null) Console.instance.Print(l);
                else plugin.Log.LogInfo(l);
            }
        }

        // ============================================================ refusals / hostility

        internal static string IdOf(Viking v)
        {
            ZNetView nv = v != null ? v.GetComponent<ZNetView>() : null;
            return nv != null && nv.GetZDO() != null ? nv.GetZDO().GetString(RpIdKey, "") : "";
        }

        internal static bool IsRefusing(string id, string clan)
        {
            ReputationSettings r = Cfg;
            if (r == null || !r.enabled) return false;
            if (!string.IsNullOrEmpty(id) && DialogueStages.FlagOn("grudge_" + id)) return true;
            return !string.IsNullOrEmpty(clan) && DialogueStages.FlagOn("rephostile_" + clan);
        }

        internal static List<string> RefusalLines(string id, string clan)
        {
            ReputationSettings r = Cfg;
            if (r == null) return null;
            if (!string.IsNullOrEmpty(id) && DialogueStages.FlagOn("grudge_" + id)) return r.texts.grudge;
            return r.texts.hostile;
        }

        /// <summary>Guards of a hostile faction (or of a hostile clan) attack. Vagabonds never do.</summary>
        internal static bool GuardsHostile(ManagerNPCRP.NpcDefinition npc)
        {
            ReputationSettings r = Cfg;
            if (r == null || !r.enabled || npc == null || npc.livingNorsemen == null) return false;
            if (npc.livingNorsemen.profession != "Guard") return false;

            string rf = RepFactionOf(npc);
            if (string.IsNullOrEmpty(rf) || rf == "Vagabonds" || npc.clan == "Itinerants") return false;
            return DialogueStages.FlagOn("repfhostile_" + rf) ||
                   (!string.IsNullOrEmpty(npc.clan) && DialogueStages.FlagOn("rephostile_" + npc.clan));
        }

        internal static string StateSignature(string id, ManagerNPCRP.NpcDefinition npc)
        {
            ReputationSettings r = Cfg;
            if (r == null || !r.enabled || npc == null) return "";
            string sig = "";
            if (!string.IsNullOrEmpty(id) && DialogueStages.FlagOn("grudge_" + id)) sig += "G";
            else if (!string.IsNullOrEmpty(npc.clan) && DialogueStages.FlagOn("rephostile_" + npc.clan)) sig += "H";
            if (GuardsHostile(npc)) sig += "A";
            return sig + LocalCareer();
        }

        internal static void Overlay(Dictionary<string, List<string>> lists, string id, string clan)
        {
            if (!IsRefusing(id, clan)) return;
            List<string> lines = RefusalLines(id, clan);
            if (lines == null || lines.Count == 0) return;
            lists["Greets"] = new List<string>(lines);
            lists["Generic"] = new List<string>(lines);
            lists["Farewells"] = new List<string>(lines);
        }

        // Blocks the native Norsemen interaction when this NPC refuses the player.
        internal static bool VikingInteractPrefix(Viking __instance, Humanoid __0, ref bool __result)
        {
            try
            {
                Player lp = __0 as Player;
                if (lp == null || lp != Player.m_localPlayer) return true;

                string id = IdOf(__instance);
                ManagerNPCRP plugin = ManagerNPCRP.Instance;
                ManagerNPCRP.NpcDefinition npc;
                if (id.Length == 0 || plugin == null || !plugin.Npcs.TryGetValue(id, out npc)) return true;
                if (!IsRefusing(id, npc.clan)) return true;

                float last;
                if (!LastRefusal.TryGetValue(id, out last) || Time.time - last > 2.5f)
                {
                    LastRefusal[id] = Time.time;
                    NpcTalk.Say(__instance, Pick(RefusalLines(id, npc.clan)), false);
                }

                __result = false;
                return false;
            }
            catch { return true; }
        }

        internal static string Pick(List<string> lines)
        {
            return lines != null && lines.Count > 0 ? lines[UnityEngine.Random.Range(0, lines.Count)] : "...";
        }

        // ============================================================ admin command

        internal static void RepCommand(string[] args)
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null || plugin.Database == null) return;
            ManagerNPCRP.NpcDatabase db = plugin.Database;
            ReputationSettings r = db.reputation;
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
            bool server = ZNet.instance != null && ZNet.instance.IsServer();

            if (sub == "status")
            {
                if (!server)
                {
                    foreach (string f in r.repFactions)
                        plugin.Log.LogInfo("  " + f + " : " + (DialogueStages.FlagOn("repfhostile_" + f) ? "HOSTILE"
                                           : TierName(r, FactionTierFromFlags(f))));
                    return;
                }

                RepWorld ws = Ws();
                foreach (KeyValuePair<string, Ledger> kv in ws.ledgers)
                {
                    plugin.Log.LogInfo("== " + kv.Key + " ==");
                    plugin.Log.LogInfo("  " + Summary(plugin, kv.Key));
                    foreach (KeyValuePair<string, int> c in kv.Value.clanPoints)
                    {
                        int nights; kv.Value.clanNights.TryGetValue(c.Key, out nights);
                        plugin.Log.LogInfo("    " + c.Key + ": " + c.Value + " pts, " + nights + " nuits");
                    }
                }
                foreach (string h in ws.history) plugin.Log.LogInfo("  history: " + h);
                List<string> awaiting = NpcLifecycle.DeadList();
                if (awaiting.Count > 0)
                    plugin.Log.LogInfo("  Awaiting respawn: " + string.Join(", ", awaiting.ToArray()));
                if (Player.m_localPlayer != null)
                    Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "Reputation: see log");
                return;
            }

            if (!server) { plugin.Log.LogInfo("rp_rep " + sub + " must run on the host/server."); return; }

            RepWorld w = Ws();
            string arg = args.Length > 2 ? args[2] : "";
            string career = args.Length > 4 ? args[4].ToLowerInvariant()
                          : args.Length > 3 && sub != "add" && sub != "set" ? args[3].ToLowerInvariant() : LocalCareer();

            if (sub == "month")
            {
                HashSet<string> active = new HashSet<string>(w.playerCareer.Values);
                if (active.Count == 0) active.Add(career);
                MonthlyUpdate(plugin, w, w.lastMonth < 0 ? 0 : w.lastMonth, active);
                Save();
            }
            else if (sub == "add" || sub == "set")
            {
                int value;
                if (args.Length < 4 || !int.TryParse(args[3], out value))
                {
                    plugin.Log.LogInfo("Usage: rp_rep add|set <Clan|Faction> <points> [career]");
                    return;
                }

                Ledger l = Led(w, career);
                if (db.cityZones.ContainsKey(arg))
                {
                    int cur; l.clanPoints.TryGetValue(arg, out cur);
                    AddClan(plugin, career, arg, sub == "add" ? value : value - cur);
                }
                else if (r.repFactions.Contains(arg))
                {
                    float cur; l.factionPoints.TryGetValue(arg, out cur);
                    AddFaction(plugin, career, arg, sub == "add" ? value : value - cur);
                }
                else { plugin.Log.LogInfo("Unknown clan/faction: " + arg); return; }

                SyncAll(plugin, career, true);
                Save();
                plugin.Log.LogInfo("[" + career + "] " + Summary(plugin, career));
            }
            else if (sub == "grudge")
            {
                RawSet("grudge_" + career + "_" + arg, true);
                plugin.Log.LogInfo("Grudge set on " + arg + " for " + career);
            }
            else if (sub == "forgive")
            {
                List<string> ids = new List<string>();
                if (arg == "all") foreach (string id in plugin.Npcs.Keys) ids.Add(id);
                else ids.Add(arg);

                int n = 0;
                foreach (string id in ids)
                    if (DialogueStages.RawOn("grudge_" + career + "_" + id)) { RawSet("grudge_" + career + "_" + id, false); n++; }
                plugin.Log.LogInfo(n + " grudge(s) cleared for " + career);
            }
        }
    }
    internal static class NpcTalk
    {
        private const string HeardKey = "RP_Heard";
        private const string TalksPrefix = "RP_Talks_";
        private sealed class KnowEntry
        {
            public string text = "";
            public List<string> unlocks = new List<string>();
            public List<string> needs = new List<string>();
            public List<string> forbids = new List<string>();
            public int talks;
            public int rep;
        }
        internal static void Say(Viking v, string text, bool large)
        {
            if (v == null || Chat.instance == null || string.IsNullOrEmpty(text)) return;
            float ttl = Mathf.Clamp(2.5f + text.Length * 0.055f, 3.5f, 12f);
            Chat.instance.SetNpcText(v.gameObject, Vector3.up * 2.2f, 40f, ttl, "", text, large);
        }
        private static List<string> StrList(JToken t)
        {
            List<string> l = new List<string>();
            JArray a = t as JArray;
            if (a != null) foreach (JToken x in a) l.Add((string)x);
            return l;
        }
        private static List<KnowEntry> ParseKnows(ManagerNPCRP.NpcDefinition npc)
        {
            List<KnowEntry> list = new List<KnowEntry>();
            JToken sc;
            if (npc.extra == null || !npc.extra.TryGetValue("scenario", out sc)) return list;
            JArray arr = sc["knows"] as JArray;
            if (arr == null) return list;
            foreach (JToken k in arr)
            {
                KnowEntry e = new KnowEntry();
                e.text = (string)k["text"] ?? "";
                e.talks = k["talks"] != null ? (int)k["talks"] : 0;
                e.rep = k["rep"] != null ? (int)k["rep"] : 0;
                string u = (string)k["unlocks"];
                if (!string.IsNullOrEmpty(u)) e.unlocks.Add(u);
                e.needs = StrList(k["needs"]);
                e.forbids = StrList(k["forbids"]);
                list.Add(e);
            }
            return list;
        }
        private static HashSet<string> Heard(Player p)
        {
            string raw;
            return p.m_customData.TryGetValue(HeardKey, out raw) && !string.IsNullOrEmpty(raw)
                ? new HashSet<string>(raw.Split(',')) : new HashSet<string>();
        }
        private static int Talks(Player p, string id)
        {
            string raw; int n;
            return p.m_customData.TryGetValue(TalksPrefix + id, out raw) && int.TryParse(raw, out n) ? n : 0;
        }
        // No Player.TakeInput() (not accessible): look for an open chat / console / menu through reflection.
        private static bool UiBlocked()
        {
            try
            {
                if (Chat.instance != null)
                {
                    MethodInfo focus = typeof(Chat).GetMethod("HasFocus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (focus != null && (bool)focus.Invoke(Chat.instance, null)) return true;
                }
                foreach (Type t in new[] { typeof(Console), typeof(Menu) })
                {
                    MethodInfo visible = t.GetMethod("IsVisible", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (visible != null && (bool)visible.Invoke(null, null)) return true;
                }
            }
            catch { }
            return false;
        }
        internal static void TryTalk()
        {
            Player lp = Player.m_localPlayer;
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (lp == null || plugin == null || plugin.Database == null || UiBlocked()) return;
            Viking best = null;
            string bestId = null;
            ManagerNPCRP.NpcDefinition bestNpc = null;
            float bestD = 3.5f;
            foreach (Character c in Character.GetAllCharacters())
            {
                Viking v = c as Viking;
                if (v == null || v.IsDead()) continue;
                float d = Vector3.Distance(v.transform.position, lp.transform.position);
                if (d >= bestD) continue;
                ZNetView nv = v.GetComponent<ZNetView>();
                if (nv == null || nv.GetZDO() == null) continue;
                string id = nv.GetZDO().GetString("NorsemanNPC_RP_ID", "");
                if (id.Length == 0) continue;
                ManagerNPCRP.NpcDefinition npc;
                if (!plugin.Npcs.TryGetValue(id, out npc) &&
                    !PopulationManager.TryResolveGenerated(nv.GetZDO(), id, out npc)) continue;
                best = v; bestId = id; bestNpc = npc; bestD = d;
            }
            if (best != null) Talk(lp, best, bestId, bestNpc);
        }
        private static void Talk(Player lp, Viking v, string id, ManagerNPCRP.NpcDefinition npc)
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            ReputationSettings rep = plugin.Database.reputation;
            // Killed-and-respawned NPC, or a clan hostile to the company: no conversation.
            if (ReputationManager.IsRefusing(id, npc.clan))
            {
                Say(v, ReputationManager.Pick(ReputationManager.RefusalLines(id, npc.clan)), true);
                return;
            }
            int tier = ReputationManager.TierFor(npc);
            int talks = Talks(lp, id) + 1;
            lp.m_customData[TalksPrefix + id] = talks.ToString();
            HashSet<string> heard = Heard(lp);
            List<KnowEntry> knows = ParseKnows(npc);
            KnowEntry chosen = null;
            string chosenId = null;
            bool gatedByRep = false;
            for (int i = 0; i < knows.Count; i++)
            {
                string kid = id + "#" + i;
                KnowEntry k = knows[i];
                if (heard.Contains(kid) || talks < k.talks) continue;
                if (k.needs.Any(f => !DialogueStages.FlagOn(f)) || k.forbids.Any(DialogueStages.FlagOn)) continue;
                if (tier < k.rep) { gatedByRep = true; continue; }
                chosen = k; chosenId = kid;
                break;
            }
            if (chosen != null)
            {
                Say(v, chosen.text, true);
                heard.Add(chosenId);
                lp.m_customData[HeardKey] = string.Join(",", heard.ToArray());
                foreach (string flag in chosen.unlocks)
                    if (!DialogueStages.FlagOn(flag) && ZoneSystem.instance != null)
                        ZoneSystem.instance.SetGlobalKey("rp_" + flag);
                lp.Message(MessageHud.MessageType.TopLeft, "Journal mis à jour : " + npc.identity.name);
                return;
            }
            if (gatedByRep && rep.texts.reserved.Count > 0)
            {
                Say(v, ReputationManager.Pick(rep.texts.reserved[Mathf.Min(tier, rep.texts.reserved.Count - 1)]), true);
                return;
            }
            ManagerNPCRP.DialogueSetDefinition set = plugin.ResolveDialogueSet(npc);
            List<string> generic = set != null ? DialogueStages.Effective(set)["Generic"] : null;
            Say(v, ReputationManager.Pick(generic), true);
        }
        internal static void JournalCommand()
        {
            Player lp = Player.m_localPlayer;
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (lp == null || plugin == null || plugin.Database == null) return;
            ManagerNPCRP.NpcDatabase db = plugin.Database;
            ReputationSettings rep = db.reputation;
            string career = ReputationManager.LocalCareer();
            CareerDefinition cd;
            List<string> lines = new List<string>();
            lines.Add("=== Journal - " + (db.careers.TryGetValue(career, out cd) ? cd.name : career) + " ===");
            lines.Add("Factions :");
            foreach (string f in rep.repFactions)
            {
                string st = DialogueStages.FlagOn("repfhostile_" + f) ? "HOSTILE"
                          : rep.tierNames[Mathf.Min(ReputationManager.FactionTierFromFlags(f), rep.tierNames.Count - 1)];
                lines.Add("  " + f + " : " + st);
            }
            lines.Add("Villes :");
            foreach (string clan in db.cityZones.Keys)
            {
                if (DialogueStages.FlagOn("rephostile_" + clan)) { lines.Add("  " + clan + " : HOSTILE"); continue; }
                int t = ReputationManager.TierFromFlags(clan);
                if (t > 0) lines.Add("  " + clan + " : " + rep.tierNames[Mathf.Min(t, rep.tierNames.Count - 1)]);
            }
            lines.Add("Indices entendus :");
            foreach (string kid in Heard(lp))
            {
                int h = kid.IndexOf('#');
                if (h < 0) continue;
                string nid = kid.Substring(0, h);
                int idx;
                ManagerNPCRP.NpcDefinition npc;
                if (!int.TryParse(kid.Substring(h + 1), out idx) || !plugin.Npcs.TryGetValue(nid, out npc)) continue;
                List<KnowEntry> knows = ParseKnows(npc);
                if (idx < knows.Count) lines.Add("  [" + npc.identity.name + "] " + knows[idx].text);
            }
            foreach (string l in lines)
            {
                if (Console.instance != null) Console.instance.Print(l);
                else plugin.Log.LogInfo(l);
            }
        }
    }
    // =====================================================================
    // STORY STAGES (dialogue lines unlocked by world flags)
    // =====================================================================
    [Serializable]
    public sealed class DialogueStage
    {
        public string id;
        public List<string> requires = new List<string>();   // all must be set
        public List<string> forbids = new List<string>();    // none may be set
        public List<string> replace = new List<string>();    // categories whose earlier lines are dropped
        public List<string> Greets = new List<string>();
        public List<string> Farewells = new List<string>();
        public List<string> Generic = new List<string>();
        public List<string> PlayerBase = new List<string>();
        public List<string> Damaged = new List<string>();
        public List<string> Thieved = new List<string>();
        public List<string> Puke = new List<string>();
        public List<string> Eat = new List<string>();
    }
    internal static class DialogueStages
    {
        private const string FlagPrefix = "rp_";
        private static readonly string[] Categories =
            { "Greets", "Farewells", "Generic", "PlayerBase", "Damaged", "Thieved", "Puke", "Eat" };
        /// <summary>Story flags are world-wide; rep_/repf_/grudge_ flags are resolved for the LOCAL player's career.</summary>
        internal static bool FlagOn(string flag)
        {
            if (ReputationManager.IsScoped(flag)) flag = ReputationManager.Scope(flag);
            return RawOn(flag);
        }
        internal static bool RawOn(string flag)
        {
            return ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(FlagPrefix + flag);
        }
        private static bool Active(DialogueStage st)
        {
            if (st == null) return false;
            if (st.requires != null)
                foreach (string f in st.requires) if (!FlagOn(f)) return false;
            if (st.forbids != null)
                foreach (string f in st.forbids) if (FlagOn(f)) return false;
            return true;
        }
        private static List<string> Lines(ManagerNPCRP.DialogueSetDefinition s, string cat)
        {
            switch (cat)
            {
                case "Greets": return s.Greets;
                case "Farewells": return s.Farewells;
                case "Generic": return s.Generic;
                case "PlayerBase": return s.PlayerBase;
                case "Damaged": return s.Damaged;
                case "Thieved": return s.Thieved;
                case "Puke": return s.Puke;
                default: return s.Eat;
            }
        }
        private static List<string> Lines(DialogueStage s, string cat)
        {
            switch (cat)
            {
                case "Greets": return s.Greets;
                case "Farewells": return s.Farewells;
                case "Generic": return s.Generic;
                case "PlayerBase": return s.PlayerBase;
                case "Damaged": return s.Damaged;
                case "Thieved": return s.Thieved;
                case "Puke": return s.Puke;
                default: return s.Eat;
            }
        }
        /// <summary>Base lines + every active stage (in file order); "replace" drops what came before.</summary>
        internal static Dictionary<string, List<string>> Effective(ManagerNPCRP.DialogueSetDefinition set)
        {
            Dictionary<string, List<string>> result = new Dictionary<string, List<string>>();
            foreach (string cat in Categories)
            {
                List<string> baseLines = Lines(set, cat);
                result[cat] = baseLines != null ? new List<string>(baseLines) : new List<string>();
            }
            if (set.stages == null)
                return result;

            foreach (DialogueStage st in set.stages)
            {
                if (!Active(st)) continue;
                foreach (string cat in Categories)
                {
                    List<string> add = Lines(st, cat);
                    if (add == null || add.Count == 0) continue;
                    if (st.replace != null && st.replace.Contains(cat)) result[cat].Clear();
                    result[cat].AddRange(add);
                }
            }
            return result;
        }
        internal static string Signature(ManagerNPCRP.DialogueSetDefinition set)
        {
            if (set.stages == null || set.stages.Count == 0) return "";
            List<string> active = new List<string>();
            foreach (DialogueStage st in set.stages)
                if (Active(st)) active.Add(st.id);
            return string.Join(",", active.ToArray());
        }
        internal static void RelayCommand()
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            JToken relay;
            if (plugin == null || plugin.Database == null || plugin.Database.extra == null ||
                !plugin.Database.extra.TryGetValue("relay", out relay)) return;
            JArray corridors = relay["corridors"] as JArray;
            if (corridors == null) return;
            List<string> lines = new List<string>();
            foreach (JToken c in corridors)
            {
                lines.Add((RawOn((string)c["doneFlag"]) ? "[x] " : "[ ] ") + (string)c["name"] + " -> " + (string)c["goal"]);
                foreach (JToken st in (JArray)c["stations"])
                {
                    bool done = RawOn((string)st["flag"]);
                    lines.Add("    [" + (done ? "x" : " ") + "] " + (string)st["clan"] + " (" + (string)st["biome"] + ", " +
                              (string)st["cult"] + ")" + (done ? " -> " + (string)st["leadsTo"] : ""));
                }
            }
            foreach (string l in lines)
            {
                if (Console.instance != null) Console.instance.Print(l);
                else plugin.Log.LogInfo(l);
            }
        }
        internal static void FlagCommand(string[] args)
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null || plugin.Database == null || ZoneSystem.instance == null)
                return;
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            string flag = args.Length > 2 ? args[2] : "";
            if (sub == "set" || sub == "clear")
            {
                if (string.IsNullOrWhiteSpace(flag))
                {
                    plugin.Log.LogInfo("Usage: rp_flag set|clear <flag>");
                    return;
                }
                if (sub == "set") ZoneSystem.instance.SetGlobalKey(FlagPrefix + flag);
                else ZoneSystem.instance.RemoveGlobalKey(FlagPrefix + flag);
                string msg = "Flag " + flag + (sub == "set" ? " SET" : " cleared");
                plugin.Log.LogInfo(msg);
                if (Player.m_localPlayer != null)
                    Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, msg);
                return;
            }
            // list: read the thread registry kept in the JSON ("lore")
            JToken lore;
            if (plugin.Database.extra == null || !plugin.Database.extra.TryGetValue("lore", out lore))
            {
                plugin.Log.LogInfo("No 'lore' registry in the database.");
                return;
            }
            JObject threads = lore["threads"] as JObject;
            if (threads == null) return;
            foreach (JProperty thread in threads.Properties())
            {
                plugin.Log.LogInfo("== " + (string)thread.Value["title"] + " (" + thread.Name + ")");
                foreach (JToken f in thread.Value["flags"])
                {
                    string id = (string)f["id"];
                    plugin.Log.LogInfo("   [" + (FlagOn(id) ? "x" : " ") + "] " + id + " - " + (string)f["desc"]);
                }
            }
        }
    }
    // =====================================================================
    // PLACEMENT  (formerly NpcPlacement.cs)
    // =====================================================================
    [Serializable]
    public sealed class PlacementSettings
    {
        public bool runtime = true;              // solve placement when an un-baked NPC is first instantiated
        public float maxSearchRadius = 28f;
        public float ringStep = 2f;
        public float minSpacing = 1.6f;
        public float minWaterClearance = 0.35f;
        public float maxSlopeDegrees = 38f;
        public bool preferOpenSky = true;
        public float coveredPenalty = 6f;        // distance-equivalent penalty for spots under a roof
        public float scanMaxPlayerDistance = 60f;
        public int maxAttempts = 3;               // failed placements per NPC before giving up (rp_check fix re-opens)
        public float fallbackRadiusFactor = 2f;   // second chance: wider search around spawn then home
    }
    internal struct PlacementResult
    {
        public bool found;
        public bool zoneNotReady;
        public bool covered;
        public Vector3 position;
        public string reason;
    }
    /// <summary>
    /// Finds a valid standing spot near a target: real terrain/floor height (colliders, not estimates),
    /// not in water, not on a cliff, not inside a building piece or another character.
    /// Needs the zone to be loaded (physics colliders exist) - hence the runtime + "rp_scan" bake workflow.
    /// </summary>
    internal static class PlacementReport
    {
        private static readonly List<string> Lines = new List<string>();
        internal static void Add(string id, string name, string reason)
        {
            Lines.Add(DateTime.Now.ToString("HH:mm:ss") + " [" + id + "] " + name + ": " + reason);
            if (Lines.Count > 500) Lines.RemoveAt(0);
            ManagerNPCRP.Instance?.Log.LogWarning("placement [" + id + "] " + name + ": " + reason);
        }
        internal static void Write(IEnumerable<string> extra = null)
        {
            try
            {
                List<string> all = new List<string>(Lines);
                if (extra != null) all.AddRange(extra);
                File.WriteAllText(Path.Combine(Paths.ConfigPath, "Norseman-NPC-RP", "placement_report.txt"),
                                  string.Join("\n", all.ToArray()));
            }
            catch { }
        }
    }
    internal static class NpcPlacement
    {
        internal const string PlacedKey = "NorsemanNPC_Placed";
        internal const string FailsKey = "NorsemanNPC_PlaceFails";
        internal const string GaveUpKey = "NorsemanNPC_PlaceGaveUp";
        private static bool _masksReady;
        private static int _terrainMask;
        private static int _structureMask;
        private static int _solidMask;
        private static int _characterMask;
        private static readonly Collider[] Buffer = new Collider[48];
        private static void InitMasks()
        {
            if (_masksReady)
                return;

            _terrainMask = LayerMask.GetMask("terrain");
            _structureMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "vehicle");
            _solidMask = _structureMask | _terrainMask;
            _characterMask = LayerMask.GetMask("character", "character_net");
            _masksReady = true;
        }
        internal static PlacementResult Solve(
            Vector3 target, PlacementSettings cfg, IList<Vector3> taken,
            Transform ignore, bool includeCharacters)
        {
            InitMasks();
            if (cfg == null)
                cfg = new PlacementSettings();

            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;

            RaycastHit probe;
            if (!Physics.Raycast(new Vector3(target.x, 600f, target.z), Vector3.down, out probe,
                    1200f, _terrainMask, QueryTriggerInteraction.Ignore))
            {
                return new PlacementResult { zoneNotReady = true, reason = "terrain collider not loaded" };
            }
            bool found = false;
            bool bestCovered = false;
            Vector3 best = target;
            float bestScore = float.MaxValue;
            string lastReason = "no candidate";
            float step = Mathf.Max(1f, cfg.ringStep);
            for (float r = 0f; r <= cfg.maxSearchRadius; r += step)
            {
                int count = r < 0.01f ? 1 : Mathf.Max(6, Mathf.CeilToInt(2f * Mathf.PI * r / step));
                for (int i = 0; i < count; i++)
                {
                    float a = (float)i / count * Mathf.PI * 2f;
                    Vector3 c = new Vector3(target.x + Mathf.Cos(a) * r, 0f, target.z + Mathf.Sin(a) * r);
                    Vector3 pos;
                    bool covered;
                    string why;
                    if (!Evaluate(c, cfg, taken, ignore, includeCharacters, water, out pos, out covered, out why))
                    {
                        lastReason = why;
                        continue;
                    }
                    float score = r + (covered && cfg.preferOpenSky ? cfg.coveredPenalty : 0f);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = pos;
                        bestCovered = covered;
                        found = true;
                    }
                }
                if (found && bestScore <= r + step)
                    break;
            }
            if (!found)
                return new PlacementResult { reason = lastReason };
            return new PlacementResult { found = true, position = best, covered = bestCovered };
        }
        private static bool Evaluate(
            Vector3 c, PlacementSettings cfg, IList<Vector3> taken, Transform ignore,
            bool includeCharacters, float water, out Vector3 pos, out bool covered, out string why)
        {
            pos = c;
            covered = false;
            why = null;
            RaycastHit ground;
            if (!Physics.Raycast(new Vector3(c.x, 600f, c.z), Vector3.down, out ground,
                    1200f, _terrainMask, QueryTriggerInteraction.Ignore))
            {
                why = "zone not loaded";
                return false;
            }
            float groundY = ground.point.y;
            if (groundY < water + cfg.minWaterClearance)
            {
                why = "water";
                return false;
            }
            if (Vector3.Angle(ground.normal, Vector3.up) > cfg.maxSlopeDegrees)
            {
                why = "slope too steep";
                return false;
            }
            // Standing surface: terrain, or a floor piece/rock within 2 m above the ground.
            float standY = groundY;
            RaycastHit surface;
            if (Physics.Raycast(new Vector3(c.x, groundY + 2f, c.z), Vector3.down, out surface,
                    2.6f, _solidMask, QueryTriggerInteraction.Ignore))
            {
                standY = Mathf.Max(groundY, surface.point.y);
            }
            Vector3 p = new Vector3(c.x, standY, c.z);
            // Capsule must be free of walls, furniture, trees, other characters.
            int mask = _structureMask | (includeCharacters ? _characterMask : 0);
            int n = Physics.OverlapCapsuleNonAlloc(
                p + Vector3.up * 0.45f, p + Vector3.up * 1.75f, 0.4f, Buffer, mask,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                Collider col = Buffer[i];
                if (col == null)
                    continue;
                if (ignore != null && col.transform.root == ignore.root)
                    continue;

                why = "obstructed by " + col.name;
                return false;
            }
            if (taken != null)
            {
                float min2 = cfg.minSpacing * cfg.minSpacing;
                for (int i = 0; i < taken.Count; i++)
                {
                    float dx = taken[i].x - p.x;
                    float dz = taken[i].z - p.z;
                    if (dx * dx + dz * dz < min2)
                    {
                        why = "too close to another NPC";
                        return false;
                    }
                }
            }
            covered = Physics.Raycast(new Vector3(p.x, standY + 1.9f, p.z), Vector3.up, 8f,
                _structureMask, QueryTriggerInteraction.Ignore);
            pos = p;
            return true;
        }
        // ------------------------------------------------------------------ runtime
        private static PlacementSettings Widen(PlacementSettings c)
        {
            return new PlacementSettings
            {
                runtime = c.runtime, maxSearchRadius = c.maxSearchRadius * Mathf.Max(1f, c.fallbackRadiusFactor),
                ringStep = c.ringStep, minSpacing = c.minSpacing, minWaterClearance = c.minWaterClearance,
                maxSlopeDegrees = c.maxSlopeDegrees, preferOpenSky = c.preferOpenSky, coveredPenalty = c.coveredPenalty
            };
        }
        internal static void Move(Viking viking, ZDO zdo, Vector3 position)
        {
            Vector3 pos = position + Vector3.up * 0.1f;
            viking.transform.position = pos;
            Rigidbody body = viking.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.position = pos;
                body.linearVelocity = Vector3.zero;
            }
            zdo.SetPosition(pos);
        }
        // Last resort when nothing valid was found: at least never leave an NPC under the terrain.
        private static bool SafeLift(Viking viking, ZDO zdo)
        {
            InitMasks();
            Vector3 p = viking.transform.position;
            RaycastHit g;
            if (!Physics.Raycast(new Vector3(p.x, 600f, p.z), Vector3.down, out g, 1200f, _terrainMask,
                    QueryTriggerInteraction.Ignore)) return false;
            if (p.y >= g.point.y - 0.3f) return false;

            Move(viking, zdo, new Vector3(p.x, g.point.y + 0.3f, p.z));
            return true;
        }
        private static PlacementResult TrySolve(Viking viking, ManagerNPCRP.NpcDefinition npc, PlacementSettings cfg)
        {
            List<Vector3> others = new List<Vector3>();
            foreach (Character ch in Character.GetAllCharacters())
            {
                if (ch != null && ch != viking &&
                    Vector3.Distance(ch.transform.position, viking.transform.position) < 40f)
                    others.Add(ch.transform.position);
            }
            PlacementResult first = Solve(viking.transform.position, cfg, others, viking.transform, true);
            if (first.found || first.zoneNotReady) return first;
            // Second chance: wider search around the configured spawn, then around home.
            PlacementSettings wide = Widen(cfg);
            if (npc.locations != null)
            {
                ManagerNPCRP.SpawnPosition[] anchors = { npc.locations.spawn, npc.locations.home };
                foreach (ManagerNPCRP.SpawnPosition a in anchors)
                {
                    if (a == null || !a.enabled) continue;
                    PlacementResult r2 = Solve(new Vector3(a.x, a.y, a.z), wide, others, viking.transform, true);
                    if (r2.found) return r2;
                }
            }
            return first;   // keeps the original failure reason
        }
        internal static IEnumerator PlaceWhenReady(
            Viking viking, ZNetView nview, ManagerNPCRP.NpcDefinition npc, PlacementSettings cfg)
        {
            yield return new WaitForSeconds(0.75f);

            ZDO first = nview != null ? nview.GetZDO() : null;
            string id = first != null ? first.GetString("NorsemanNPC_RP_ID", "?") : "?";
            string label = npc.identity != null ? npc.identity.name : id;

            for (int attempt = 0; attempt < 40; attempt++)
            {
                if (viking == null || nview == null || !nview.IsValid()) yield break;

                PlacementResult r = TrySolve(viking, npc, cfg);
                if (r.zoneNotReady)
                {
                    yield return new WaitForSeconds(0.5f);
                    continue;
                }

                ZDO zdo = nview.GetZDO();
                if (zdo == null || !nview.IsOwner()) yield break;

                if (r.found)
                {
                    Move(viking, zdo, r.position);

                    if (npc.behavior != null && npc.behavior.isStatic)
                    {
                        MonsterAI ai = viking.GetComponent<MonsterAI>();
                        if (ai != null) ai.SetPatrolPoint();
                    }

                    zdo.Set(PlacedKey, true);
                    zdo.Set(FailsKey, 0);
                    zdo.Set(GaveUpKey, false);
                    yield break;
                }

                // Failure: never mark as placed on a failure, retry on the next load, give up after maxAttempts.
                int fails = zdo.GetInt(FailsKey, 0) + 1;
                zdo.Set(FailsKey, fails);
                PlacementReport.Add(id, label, "attempt " + fails + "/" + cfg.maxAttempts + ": " + r.reason);

                if (fails >= cfg.maxAttempts)
                {
                    bool lifted = SafeLift(viking, zdo);
                    zdo.Set(PlacedKey, true);    // stop retrying; "rp_check fix" re-opens it
                    zdo.Set(GaveUpKey, true);
                    PlacementReport.Add(id, label, "GAVE UP" + (lifted ? " (lifted above the terrain)" : ""));
                    PlacementReport.Write();
                }
                yield break;
            }

            PlacementReport.Add(id, label, "terrain colliders never loaded (20 s): retried on the next load");
        }
        // ------------------------------------------------------------------ rp_check [Clan|NPC-ID|all] [fix]
        internal static void CheckCommand(string[] args)
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null || plugin.Database == null) return;
            InitMasks();
            string filter = args.Length > 1 && !args[1].Equals("fix", StringComparison.OrdinalIgnoreCase) ? args[1] : "all";
            bool fix = args.Any(a => a.Equals("fix", StringComparison.OrdinalIgnoreCase));
            PlacementSettings cfg = plugin.Database.settings.placement ?? new PlacementSettings();
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            int total = 0, ok = 0, noZone = 0, under = 0, inWater = 0, stuck = 0, floating = 0, drifted = 0, fixedCount = 0;
            List<string> lines = new List<string>();
            foreach (Character c in Character.GetAllCharacters())
            {
                Viking v = c as Viking;
                if (v == null || v.IsDead()) continue;
                ZNetView nv = v.GetComponent<ZNetView>();
                if (nv == null || nv.GetZDO() == null) continue;
                string id = nv.GetZDO().GetString("NorsemanNPC_RP_ID", "");
                if (id.Length == 0) continue;
                ManagerNPCRP.NpcDefinition npc;
                bool named = plugin.Npcs.TryGetValue(id, out npc);
                if (!named && !PopulationManager.TryResolveGenerated(nv.GetZDO(), id, out npc)) continue;
                bool match = filter.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                             id.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(npc.clan, filter, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                total++;
                Vector3 p = v.transform.position;
                RaycastHit g;
                if (!Physics.Raycast(new Vector3(p.x, 600f, p.z), Vector3.down, out g, 1200f, _terrainMask,
                        QueryTriggerInteraction.Ignore))
                {
                    noZone++;
                    continue;
                }
                List<string> issues = new List<string>();
                if (p.y < g.point.y - 0.6f) { issues.Add("underground"); under++; }
                else if (g.point.y < water - 0.05f && p.y < water + 0.3f) { issues.Add("in water"); inWater++; }
                RaycastHit surface;
                float surfaceY = g.point.y;
                if (Physics.Raycast(p + Vector3.up, Vector3.down, out surface, 8f, _solidMask, QueryTriggerInteraction.Ignore))
                    surfaceY = surface.point.y;
                if (p.y - surfaceY > 3f) { issues.Add("floating " + (p.y - surfaceY).ToString("0.0") + " m"); floating++; }
                int n = Physics.OverlapCapsuleNonAlloc(p + Vector3.up * 0.45f, p + Vector3.up * 1.75f, 0.35f, Buffer,
                    _structureMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    if (Buffer[i] != null && Buffer[i].transform.root != v.transform.root)
                    {
                        issues.Add("stuck in " + Buffer[i].name);
                        stuck++;
                        break;
                    }
                }
                if (named && npc.behavior != null && npc.behavior.isStatic && npc.locations != null && npc.locations.spawn != null)
                {
                    float dx = p.x - npc.locations.spawn.x, dz = p.z - npc.locations.spawn.z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d > 15f) { issues.Add("drifted " + d.ToString("0") + " m from its post"); drifted++; }
                }
                if (issues.Count == 0) { ok++; continue; }
                lines.Add("[" + id + "] " + npc.identity.name + " (" + npc.clan + ") @ " +
                          p.x.ToString("0") + "," + p.y.ToString("0.0") + "," + p.z.ToString("0") + " : " + string.Join(", ", issues.ToArray()));
                if (fix)
                {
                    nv.ClaimOwnership();
                    ZDO z = nv.GetZDO();
                    z.Set(PlacedKey, false);
                    z.Set(FailsKey, 0);
                    z.Set(GaveUpKey, false);
                    plugin.StartCoroutine(PlaceWhenReady(v, nv, npc, cfg));
                    fixedCount++;
                }
            }
            string summary = "rp_check " + filter + ": " + total + " loaded, " + ok + " OK, " + under + " underground, " +
                             inWater + " in water, " + stuck + " stuck, " + floating + " floating, " + drifted + " drifted, " +
                             noZone + " skipped (terrain not loaded)" + (fix ? " | re-placing " + fixedCount : ".");
            plugin.Log.LogInfo(summary);
            foreach (string l in lines.Take(60)) plugin.Log.LogInfo("  " + l);
            lines.Insert(0, summary);
            PlacementReport.Write(lines);
            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft,
                    "Check: " + ok + "/" + total + " OK, " + (total - ok - noZone) + " problem(s) - see log");
        }
        // ------------------------------------------------------------------ bake command
        /// <summary>rp_scan &lt;Clan|NPC-ID|all&gt; [dry] - run near the city, writes real positions to the JSON.</summary>
        internal static void ScanCommand(string[] args)
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            Player player = Player.m_localPlayer;
            if (plugin == null || plugin.Database == null || player == null)
                return;
            string target = args.Length > 1 ? args[1] : "";
            bool dry = args.Any(a => a.Equals("dry", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(target))
            {
                plugin.Log.LogInfo("Usage: rp_scan <Clan|NPC-ID|all> [dry]  (stand inside the city first)");
                return;
            }
            PlacementSettings cfg = plugin.Database.settings.placement ?? new PlacementSettings();
            Dictionary<string, List<Vector3>> takenByClan = new Dictionary<string, List<Vector3>>();
            int ok = 0, failed = 0, skipped = 0, moved = 0, points = 0;
            foreach (KeyValuePair<string, ManagerNPCRP.NpcDefinition> kv in plugin.Npcs)
            {
                ManagerNPCRP.NpcDefinition npc = kv.Value;
                bool match = target.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                             kv.Key.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(npc.clan, target, StringComparison.OrdinalIgnoreCase);
                if (!match || npc.locations == null || npc.locations.spawn == null)
                    continue;
                ManagerNPCRP.SpawnPosition sp = npc.locations.spawn;
                Vector3 from = new Vector3(sp.x, sp.y, sp.z);
                Vector3 flat = player.transform.position - from;
                flat.y = 0f;
                if (flat.magnitude > cfg.scanMaxPlayerDistance)
                {
                    skipped++;
                    continue;
                }
                List<Vector3> taken;
                if (!takenByClan.TryGetValue(npc.clan ?? "", out taken))
                    takenByClan[npc.clan ?? ""] = taken = new List<Vector3>();
                PlacementResult r = Solve(from, cfg, taken, null, false);
                if (!r.found)
                {
                    failed++;
                    PlacementReport.Add(kv.Key, npc.identity.name, "rp_scan: " + r.reason);
                    continue;
                }
                float shift = new Vector2(r.position.x - sp.x, r.position.z - sp.z).magnitude;
                if (shift > 6f)
                {
                    moved++;
                    plugin.Log.LogInfo($"rp_scan [{kv.Key}] {npc.identity.name} moved {shift:0.0} m" +
                                       (r.covered ? " (indoors)" : ""));
                }
                taken.Add(r.position);
                ok++;
                if (!dry)
                {
                    sp.x = r.position.x;
                    sp.y = r.position.y;
                    sp.z = r.position.z;
                    sp.baked = true;
                }
                points += BakePoint(npc.locations.home, cfg, dry);
                points += BakePoint(npc.locations.work, cfg, dry);
                points += BakePoint(npc.locations.patrolPointB, cfg, dry);
            }
            if (!dry && ok > 0)
                plugin.SaveDatabaseInternal();
            string msg = $"rp_scan{(dry ? " (dry)" : "")}: {ok} placed, {moved} moved >6m, " +
                         $"{failed} failed, {skipped} too far, {points} routine points";
            plugin.Log.LogInfo(msg);
            PlacementReport.Write();
            player.Message(MessageHud.MessageType.TopLeft, msg);
        }
        private static int BakePoint(ManagerNPCRP.SpawnPosition p, PlacementSettings cfg, bool dry)
        {
            if (p == null || !p.enabled)
                return 0;
            PlacementResult r = Solve(new Vector3(p.x, p.y, p.z), cfg, null, null, false);
            if (!r.found)
                return 0;
            if (!dry)
            {
                p.x = r.position.x;
                p.y = r.position.y;
                p.z = r.position.z;
                p.baked = true;
            }
            return 1;
        }
    }
    // =====================================================================
    // POPULATION / GROUPS / WANDERING  (formerly NpcPopulation.cs)
    // =====================================================================
    // ------------------------------------------------------------------ JSON contracts (schema v3)
    [Serializable]
    public sealed class FactionPopulation
    {
        public int maxPopulation = 30;        // hard cap of GENERATED citizens for the whole faction
        public int targetPopulation = 12;     // immigration stops once this is reached
        public int spawnPerTick = 1;          // immigrants per tick per clan
        public float reproductionRate = 0.03f; // expected births per couple per tick
    }
    [Serializable]
    public sealed class NamePools
    {
        public List<string> male = new List<string>();
        public List<string> female = new List<string>();
        public Dictionary<string, List<string>> epithets = new Dictionary<string, List<string>>();
    }
    [Serializable]
    public sealed class PopulationSettings
    {
        public bool enabled;                   // opt-in: rp_pop on
        public float tickSeconds = 300f;       // world-time interval (5 min)
        public int maxGeneratedTotal = 120;    // absolute cap for the whole world
        public bool requirePlayerNearby = true;
        public float activationRadius = 160f;
        public bool reproductionEnabled = true;
        public Dictionary<string, FactionPopulation> factions = new Dictionary<string, FactionPopulation>();
        public NamePools names = new NamePools();
    }
    [Serializable]
    public sealed class CityZone
    {
        public float x;
        public float z;
        public float radius = 45f;             // spawn area
        public float wanderRadius = 35f;       // walking area
        public int maxCitizens = 6;            // generated citizens living in this city
        public List<string> professions = new List<string>();
    }
    [Serializable]
    public sealed class GroupBehavior
    {
        public bool enabled = true;
        public int maxGroupSize = 4;
        public float groupChance = 0.5f;       // chance an immigrant batch forms a group
        public float followRadius = 5f;
        public float wanderMinSeconds = 25f;
        public float wanderMaxSeconds = 75f;
        public bool wanderGenerated = true;
        public bool wanderNamed;               // named NPCs keep LivingNorsemen/static behaviour by default
    }
    public sealed class NpcGroup
    {
        public string id;
        public string clan;
        public string leaderId;
        public readonly List<ZDO> members = new List<ZDO>();
        public bool HasRoom(int max) { return members.Count < max; }
    }
    public sealed class PopWorldState
    {
        public double lastTick;
        public int counter;
        public Dictionary<string, int> pending = new Dictionary<string, int>();
    }
    public sealed class PopState
    {
        public Dictionary<string, PopWorldState> worlds = new Dictionary<string, PopWorldState>();
    }
    // ------------------------------------------------------------------ manager
    internal static class PopulationManager
    {
        // Same keys as ManagerNPCRP (kept in sync on purpose).
        private const string RpIdKey = "NorsemanNPC_RP_ID";
        private const string ClanKey = "NorsemanNPC_Clan";
        private const string RoleKey = "NorsemanNPC_Role";
        private const string ProfessionKey = "NorsemanNPC_Profession";
        internal const string GenKey = "RP_Generated";
        internal const string GenNameKey = "RP_GenName";
        internal const string GenGenderKey = "RP_GenGender";
        internal const string BornKey = "RP_Born";
        internal const string GroupIdKey = "RP_GroupId";
        internal const string GroupRoleKey = "RP_GroupRole";      // leader | follower
        internal const string GroupLeaderKey = "RP_GroupLeader";  // leader RP id
        internal const string TargetXKey = "RP_TargetX";
        internal const string TargetZKey = "RP_TargetZ";
        private static bool _running;
        private static PopState _state;
        private static readonly Dictionary<string, ManagerNPCRP.NpcDefinition> Cache =
            new Dictionary<string, ManagerNPCRP.NpcDefinition>();
        private static readonly Dictionary<string, string> JobMasc = new Dictionary<string, string>
        { {"Cook","Cuisinier"},{"Farmer","Cultivateur"},{"Lumberjack","Bûcheron"},{"Miner","Mineur"},{"Fisherman","Pêcheur"},{"Guard","Garde"} };
        private static readonly Dictionary<string, string> JobFem = new Dictionary<string, string>
        { {"Cook","Cuisinière"},{"Farmer","Cultivatrice"},{"Lumberjack","Bûcheronne"},{"Miner","Mineuse"},{"Fisherman","Pêcheuse"},{"Guard","Gardienne"} };
        private static readonly string[] DefaultMale =
            { "Ake","Arvid","Bard","Birger","Dag","Eyvind","Gisli","Halfdan","Ivar","Kjell","Orm","Rurik","Sten","Torben","Ulf","Vigdar" };
        private static readonly string[] DefaultFemale =
            { "Alfhild","Borghild","Dagny","Estrid","Gunhild","Hallgerd","Ingebjorg","Ragna","Sunniva","Thora","Unn","Yrsa" };
        // ---------------------------------------------------------------- v2 -> v3
        internal static void MigrateToV3(ManagerNPCRP.NpcDatabase db)
        {
            if (db.population == null) db.population = new PopulationSettings();
            if (db.cityZones == null) db.cityZones = new Dictionary<string, CityZone>();
            if (db.groupBehavior == null) db.groupBehavior = new GroupBehavior();
            if (db.settings.placement == null) db.settings.placement = new PlacementSettings();
            if (db.population.factions.Count == 0)
            {
                db.population.factions["DouzeCouronnes"] = new FactionPopulation { maxPopulation = 72, targetPopulation = 24 };
                db.population.factions["CamaradesAzures"] = new FactionPopulation { maxPopulation = 24, targetPopulation = 8 };
                db.population.factions["HordeSolaire"] = new FactionPopulation { maxPopulation = 24, targetPopulation = 8 };
            }
            foreach (KeyValuePair<string, ManagerNPCRP.ClanDefinition> kv in db.clans)
            {
                if (db.cityZones.ContainsKey(kv.Key) || kv.Value == null || kv.Value.cityPosition == null)
                    continue;
                CityZone z = new CityZone { x = kv.Value.cityPosition.x, z = kv.Value.cityPosition.z };
                z.professions = db.npcs.Values
                    .Where(n => n != null && n.clan == kv.Key && n.livingNorsemen != null &&
                                !string.IsNullOrEmpty(n.livingNorsemen.profession) &&
                                n.livingNorsemen.profession != "Guard")
                    .Select(n => n.livingNorsemen.profession).Distinct().ToList();
                if (z.professions.Count == 0)
                    z.professions = new List<string> { "Cook", "Farmer" };
                db.cityZones[kv.Key] = z;
            }
        }
        // ---------------------------------------------------------------- state (persisted per world)
        private static string StatePath
        {
            get { return Path.Combine(Paths.ConfigPath, "Norseman-NPC-RP", "population_state.json"); }
        }
        private static PopWorldState GetState()
        {
            if (_state == null)
            {
                try
                {
                    _state = File.Exists(StatePath)
                        ? JsonConvert.DeserializeObject<PopState>(File.ReadAllText(StatePath))
                        : null;
                }
                catch { _state = null; }
                if (_state == null) _state = new PopState();
            }
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "unknown";
            PopWorldState ws;
            if (!_state.worlds.TryGetValue(world, out ws))
                _state.worlds[world] = ws = new PopWorldState();
            if (ws.pending == null) ws.pending = new Dictionary<string, int>();
            return ws;
        }
        private static void SaveState()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(StatePath, JsonConvert.SerializeObject(_state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                ManagerNPCRP.Instance?.Log.LogWarning("Population state not saved: " + ex.Message);
            }
        }
        // ---------------------------------------------------------------- main loop (server only)
        internal static IEnumerator Run(ManagerNPCRP plugin)
        {
            if (_running)
                yield break;
            _running = true;

            while (true)
            {
                yield return new WaitForSeconds(10f);

                ManagerNPCRP.NpcDatabase db = plugin.Database;
                if (db == null || db.population == null || !db.population.enabled)
                    continue;
                if (ZNet.instance == null || !ZNet.instance.IsServer() ||
                    ZDOMan.instance == null || ZNetScene.instance == null)
                    continue;

                PopWorldState ws = GetState();
                double now = ZNet.instance.GetTimeSeconds();

                if (ws.lastTick <= 0d || ws.lastTick > now)   // first run, or world time rewound
                {
                    ws.lastTick = now;
                    SaveState();
                    continue;
                }

                if (now - ws.lastTick < db.population.tickSeconds)
                    continue;

                ws.lastTick = now;   // never catch up several ticks: no burst after a restart
                try { Tick(plugin); }
                catch (Exception ex) { plugin.Log.LogError("Population tick failed: " + ex); }
                SaveState();
            }
        }
        // ---------------------------------------------------------------- census
        private sealed class Census
        {
            public int total;
            public readonly Dictionary<string, int> byClan = new Dictionary<string, int>();
            public readonly Dictionary<string, int> byFaction = new Dictionary<string, int>();
            public readonly Dictionary<string, int> males = new Dictionary<string, int>();
            public readonly Dictionary<string, int> females = new Dictionary<string, int>();
            public readonly Dictionary<string, NpcGroup> groups = new Dictionary<string, NpcGroup>();
            public readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public static void Inc(Dictionary<string, int> d, string k, int n = 1)
            {
                int v; d.TryGetValue(k ?? "", out v); d[k ?? ""] = v + n;
            }
            public static int Get(Dictionary<string, int> d, string k)
            {
                int v; d.TryGetValue(k ?? "", out v); return v;
            }
        }
        private static Census TakeCensus(ManagerNPCRP plugin)
        {
            Census c = new Census();
            ManagerNPCRP.NpcDatabase db = plugin.Database;

            // Named NPCs count as adults (couples) and reserve their names - they are never touched.
            foreach (ManagerNPCRP.NpcDefinition n in plugin.Npcs.Values)
            {
                if (n == null || n.clan == null) continue;
                bool female = n.identity != null && n.identity.gender == "Female";
                Census.Inc(female ? c.females : c.males, n.clan);
                if (n.identity != null && n.identity.name != null) c.names.Add(n.identity.name);
            }

            foreach (ZDO zdo in ZdoAccess.All())
            {
                if (zdo == null || !zdo.IsValid() || !zdo.GetBool(GenKey, false))
                    continue;

                string clan = zdo.GetString(ClanKey, "");
                string faction = FactionOf(db, clan);
                c.total++;
                Census.Inc(c.byClan, clan);
                Census.Inc(c.byFaction, faction);
                Census.Inc(zdo.GetString(GenGenderKey, "Male") == "Female" ? c.females : c.males, clan);
                c.names.Add(zdo.GetString(GenNameKey, ""));

                string gid = zdo.GetString(GroupIdKey, "");
                if (gid.Length > 0)
                {
                    NpcGroup g;
                    if (!c.groups.TryGetValue(gid, out g))
                        c.groups[gid] = g = new NpcGroup { id = gid, clan = clan };
                    g.members.Add(zdo);
                    if (zdo.GetString(GroupRoleKey, "") == "leader")
                        g.leaderId = zdo.GetString(RpIdKey, "");
                }
            }

            return c;
        }
        private static string FactionOf(ManagerNPCRP.NpcDatabase db, string clan)
        {
            ManagerNPCRP.ClanDefinition cd;
            return db.clans.TryGetValue(clan ?? "", out cd) && cd != null ? cd.faction : "";
        }
        private static List<Vector3> PlayerPositions()
        {
            List<Vector3> list = new List<Vector3>();
            if (Player.m_localPlayer != null)
                list.Add(Player.m_localPlayer.transform.position);
            if (ZNet.instance != null)
                foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                    list.Add(peer.m_refPos);
            return list;
        }
        private static bool NearPlayer(List<Vector3> players, CityZone z, float radius)
        {
            for (int i = 0; i < players.Count; i++)
            {
                float dx = players[i].x - z.x, dz = players[i].z - z.z;
                if (dx * dx + dz * dz <= radius * radius) return true;
            }
            return false;
        }
        // ---------------------------------------------------------------- tick
        internal static void Tick(ManagerNPCRP plugin)
        {
            ManagerNPCRP.NpcDatabase db = plugin.Database;
            PopulationSettings pop = db.population;
            PopWorldState ws = GetState();
            Census census = TakeCensus(plugin);
            List<Vector3> players = PlayerPositions();
            // virtual counters = real + pending (so queued citizens count against every cap)
            Dictionary<string, int> vClan = new Dictionary<string, int>(census.byClan);
            Dictionary<string, int> vFaction = new Dictionary<string, int>(census.byFaction);
            int vTotal = census.total;
            foreach (KeyValuePair<string, int> p in ws.pending)
            {
                string clan = p.Key.Substring(p.Key.IndexOf(':') + 1);
                Census.Inc(vClan, clan, p.Value);
                Census.Inc(vFaction, FactionOf(db, clan), p.Value);
                vTotal += p.Value;
            }
            Dictionary<string, List<string>> clansByFaction = new Dictionary<string, List<string>>();
            foreach (KeyValuePair<string, ManagerNPCRP.ClanDefinition> kv in db.clans)
            {
                if (kv.Value == null || !db.cityZones.ContainsKey(kv.Key)) continue;
                List<string> l;
                if (!clansByFaction.TryGetValue(kv.Value.faction, out l))
                    clansByFaction[kv.Value.faction] = l = new List<string>();
                l.Add(kv.Key);
            }
            int queued = 0;
            foreach (KeyValuePair<string, List<string>> fk in clansByFaction)
            {
                FactionPopulation fp;
                if (!pop.factions.TryGetValue(fk.Key, out fp)) continue;

                foreach (string clanName in fk.Value.OrderBy(x => UnityEngine.Random.value).ToList())
                {
                    CityZone zone = db.cityZones[clanName];
                    int room = Mathf.Min(zone.maxCitizens - Census.Get(vClan, clanName),
                               Mathf.Min(fp.maxPopulation - Census.Get(vFaction, fk.Key),
                                         pop.maxGeneratedTotal - vTotal));
                    if (room <= 0) continue;
                    // immigration: fills the faction up to its target
                    int immigrants = 0;
                    int gap = fp.targetPopulation - Census.Get(vFaction, fk.Key);
                    if (gap > 0)
                        immigrants = Mathf.Min(Mathf.Min(fp.spawnPerTick, gap), room);
                    // reproduction: couples in the clan (named NPCs included) x rate
                    int births = 0;
                    if (pop.reproductionEnabled && fp.reproductionRate > 0f)
                    {
                        int couples = Mathf.Min(Census.Get(census.males, clanName), Census.Get(census.females, clanName));
                        float expected = couples * fp.reproductionRate;
                        births = (int)expected + (UnityEngine.Random.value < expected - (int)expected ? 1 : 0);
                        births = Mathf.Min(births, room - immigrants);
                    }

                    if (immigrants > 0) Queue(ws, "imm:" + clanName, immigrants, vClan, vFaction, ref vTotal, clanName, fk.Key);
                    if (births > 0) Queue(ws, "birth:" + clanName, births, vClan, vFaction, ref vTotal, clanName, fk.Key);
                    queued += immigrants + births;
                }
            }
            // materialise pending citizens where a player can actually see the city
            int spawned = 0;
            foreach (string key in ws.pending.Keys.ToList())
            {
                int count = ws.pending[key];
                if (count <= 0) { ws.pending.Remove(key); continue; }
                bool born = key.StartsWith("birth:", StringComparison.Ordinal);
                string clanName = key.Substring(key.IndexOf(':') + 1);
                CityZone zone;
                if (!db.cityZones.TryGetValue(clanName, out zone)) { ws.pending.Remove(key); continue; }
                if (pop.requirePlayerNearby && !NearPlayer(players, zone, pop.activationRadius))
                    continue;
                int made = SpawnCitizens(plugin, census, clanName, zone, count, born, ws);
                spawned += made;
                ws.pending[key] = count - made;
                if (ws.pending[key] <= 0) ws.pending.Remove(key);
            }
            plugin.Log.LogInfo($"Population tick: +{queued} queued, {spawned} spawned, " +
                               $"{ws.pending.Values.Sum()} pending, {census.total + spawned} generated alive.");
        }
        private static void Queue(PopWorldState ws, string key, int n, Dictionary<string, int> vClan,
            Dictionary<string, int> vFaction, ref int vTotal, string clan, string faction)
        {
            int v; ws.pending.TryGetValue(key, out v); ws.pending[key] = v + n;
            Census.Inc(vClan, clan, n);
            Census.Inc(vFaction, faction, n);
            vTotal += n;
        }
        // ---------------------------------------------------------------- spawning
        private static string ClanPrefab(ManagerNPCRP plugin, string clan)
        {
            foreach (ManagerNPCRP.NpcDefinition n in plugin.Npcs.Values)
                if (n != null && n.clan == clan && !string.IsNullOrEmpty(n.prefab))
                    return n.prefab;
            return "Meadows_Norseman_RS";
        }
        private static int SpawnCitizens(ManagerNPCRP plugin, Census census, string clan, CityZone zone,
            int count, bool born, PopWorldState ws)
        {
            GroupBehavior gb = plugin.Database.groupBehavior ?? new GroupBehavior();
            int made = 0;
            while (made < count)
            {
                int remaining = count - made;
                NpcGroup join = null;
                if (born && gb.enabled)
                    join = census.groups.Values
                        .Where(g => g.clan == clan && g.HasRoom(gb.maxGroupSize))
                        .OrderBy(g => UnityEngine.Random.value).FirstOrDefault();
                int batch = 1;
                if (!born && gb.enabled && remaining >= 2 && UnityEngine.Random.value < gb.groupChance)
                    batch = Mathf.Min(remaining, UnityEngine.Random.Range(2, Mathf.Max(2, gb.maxGroupSize) + 1));
                NpcGroup group = join;
                if (group == null && batch > 1)
                {
                    group = new NpcGroup { id = "GRP-" + (++ws.counter), clan = clan };
                    census.groups[group.id] = group;
                }
                for (int i = 0; i < batch; i++)
                {
                    bool leader = join == null && group != null && i == 0;
                    string role = group == null ? "" : (leader ? "leader" : "follower");
                    string rpId;
                    ZDO z = SpawnCitizen(plugin, census, clan, zone, born, group, role, out rpId);
                    if (z == null) return made;
                    if (leader) group.leaderId = rpId;
                    if (group != null) group.members.Add(z);
                    made++;
                }
            }
            return made;
        }
        private static ZDO SpawnCitizen(ManagerNPCRP plugin, Census census, string clan, CityZone zone,
            bool born, NpcGroup group, string groupRole, out string rpId)
        {
            ManagerNPCRP.NpcDatabase db = plugin.Database;
            PopWorldState ws = GetState();
            rpId = "GEN-" + (++ws.counter);
            string prefabName = ClanPrefab(plugin, clan);
            int prefabHash = StringExtensionMethods.GetStableHashCode(prefabName);
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
            ZNetView pv = prefab != null ? prefab.GetComponent<ZNetView>() : null;
            if (pv == null)
            {
                plugin.Log.LogError($"Citizen spawn: prefab '{prefabName}' not found.");
                return null;
            }
            bool female = UnityEngine.Random.value < 0.5f;
            string gender = female ? "Female" : "Male";
            string prof = zone.professions.Count > 0
                ? zone.professions[UnityEngine.Random.Range(0, zone.professions.Count)] : "Farmer";
            string name = MakeName(db, clan, female, census);
            string job = (female ? JobFem : JobMasc).ContainsKey(prof) ? (female ? JobFem : JobMasc)[prof] : prof;
            string role = (born ? "Jeune " + job.ToLowerInvariant() : "Habitant — " + job);
            float ang = UnityEngine.Random.value * Mathf.PI * 2f;
            float r = Mathf.Sqrt(UnityEngine.Random.value) * zone.radius * 0.8f;
            Vector3 pos = new Vector3(zone.x + Mathf.Cos(ang) * r, 0f, zone.z + Mathf.Sin(ang) * r);
            pos.y = ManagerNPCRP.ResolveGroundHeight(pos) + 0.5f;
            ZDO zdo = ZDOMan.instance.CreateNewZDO(pos, prefabHash);
            if (zdo == null) return null;
            ZdoAccess.SetPrefab(zdo, prefabHash);
            ZdoAccess.SetRotation(zdo, new Vector3(0f, UnityEngine.Random.value * 360f, 0f));
            zdo.Persistent = true;   // generated citizens must survive restarts too
            zdo.Distant = pv.m_distant;
            zdo.Type = pv.m_type;
            zdo.Set(RpIdKey, rpId);
            zdo.Set(ClanKey, clan);
            zdo.Set(RoleKey, role);
            zdo.Set(ProfessionKey, prof);
            zdo.Set(GenKey, true);
            zdo.Set(GenNameKey, name);
            zdo.Set(GenGenderKey, gender);
            if (born) zdo.Set(BornKey, true);
            if (group != null)
            {
                zdo.Set(GroupIdKey, group.id);
                zdo.Set(GroupRoleKey, groupRole);
                if (groupRole == "follower" && !string.IsNullOrEmpty(group.leaderId))
                    zdo.Set(GroupLeaderKey, group.leaderId);
            }
            census.names.Add(name);
            census.total++;
            Census.Inc(census.byClan, clan);
            Census.Inc(census.byFaction, FactionOf(db, clan));
            Census.Inc(female ? census.females : census.males, clan);
            return zdo;
        }
        private static string MakeName(ManagerNPCRP.NpcDatabase db, string clan, bool female, Census census)
        {
            NamePools np = db.population.names ?? new NamePools();
            IList<string> firsts = female
                ? (np.female.Count > 0 ? np.female : (IList<string>)DefaultFemale)
                : (np.male.Count > 0 ? np.male : (IList<string>)DefaultMale);
            List<string> ep;
            if (!np.epithets.TryGetValue(clan, out ep) || ep.Count == 0)
                ep = new List<string> { "Sans-Nom" };
            for (int i = 0; i < 25; i++)
            {
                string n = firsts[UnityEngine.Random.Range(0, firsts.Count)] + " " +
                           ep[UnityEngine.Random.Range(0, ep.Count)];
                if (!census.names.Contains(n)) return n;
            }
            return firsts[UnityEngine.Random.Range(0, firsts.Count)] + " " + ep[0] + "-" + UnityEngine.Random.Range(2, 99);
        }
        // ---------------------------------------------------------------- generated NPC definitions
        internal static bool TryResolveGenerated(ZDO zdo, string id, out ManagerNPCRP.NpcDefinition npc)
        {
            npc = null;
            if (id == null || !id.StartsWith("GEN-", StringComparison.Ordinal))
                return false;
            if (Cache.TryGetValue(id, out npc))
                return true;
            ManagerNPCRP.NpcDatabase db = ManagerNPCRP.Instance != null ? ManagerNPCRP.Instance.Database : null;
            if (db == null || zdo == null) return false;
            string clan = zdo.GetString(ClanKey, "");
            string prof = zdo.GetString(ProfessionKey, "Farmer");
            string setName = clan + "_" + prof;
            npc = new ManagerNPCRP.NpcDefinition
            {
                identity = new ManagerNPCRP.IdentityDefinition
                {
                    name = zdo.GetString(GenNameKey, "Habitant"),
                    gender = zdo.GetString(GenGenderKey, "Male")
                },
                prefab = "",
                clan = clan,
                faction = FactionOf(db, clan),
                repFaction = FactionOf(db, clan),
                role = zdo.GetString(RoleKey, ""),
                livingNorsemen = new ManagerNPCRP.LivingNorsemenDefinition { profession = prof },
                equipmentSet = db.equipmentSets.ContainsKey(setName) ? setName : null
            };
            Cache[id] = npc;
            return true;
        }
        // ---------------------------------------------------------------- wandering
        internal static void AttachWanderer(Viking viking, ZNetView nview, ZDO zdo,
            ManagerNPCRP.NpcDefinition npc, string id)
        {
            ManagerNPCRP.NpcDatabase db = ManagerNPCRP.Instance != null ? ManagerNPCRP.Instance.Database : null;
            if (db == null || db.groupBehavior == null || !db.groupBehavior.enabled) return;
            bool generated = id.StartsWith("GEN-", StringComparison.Ordinal);
            bool allowed;
            if (generated)
                allowed = db.groupBehavior.wanderGenerated;
            else
                allowed = db.groupBehavior.wanderNamed && npc.behavior != null && !npc.behavior.isStatic &&
                          npc.locations != null && npc.locations.home == null && npc.locations.work == null;

            if (!allowed || viking.GetComponent<NpcWanderer>() != null) return;
            viking.gameObject.AddComponent<NpcWanderer>().Init(nview, id, npc);
        }
        // ---------------------------------------------------------------- commands
        private static void Say(string msg)
        {
            ManagerNPCRP.Instance?.Log.LogInfo(msg);
            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, msg);
        }
        internal static void PopCommand(string[] args)
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null || plugin.Database == null) return;
            ManagerNPCRP.NpcDatabase db = plugin.Database;
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
            if (sub == "on" || sub == "off")
            {
                db.population.enabled = sub == "on";
                plugin.SaveDatabaseInternal();
                Say("Population " + sub);
                return;
            }

            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
            {
                Say("rp_pop must run on the host/server.");
                return;
            }

            switch (sub)
            {
                case "tick":
                    Tick(plugin);
                    break;

                case "spawn":
                {
                    string clan = args.Length > 2 ? args[2] : "";
                    int n = 1;
                    if (args.Length > 3) int.TryParse(args[3], out n);
                    CityZone zone;
                    if (!db.cityZones.TryGetValue(clan, out zone)) { Say("Unknown clan zone: " + clan); return; }

                    Census census = TakeCensus(plugin);
                    n = Mathf.Min(n, db.population.maxGeneratedTotal - census.total);
                    int made = n > 0 ? SpawnCitizens(plugin, census, clan, zone, n, false, GetState()) : 0;
                    SaveState();
                    Say($"rp_pop spawn: {made} citizen(s) in {clan}");
                    break;
                }

                case "clear":
                {
                    string clan = args.Length > 2 ? args[2] : "all";
                    int removed = 0;
                    foreach (ZDO z in ZdoAccess.All().ToList())
                    {
                        if (z == null || !z.IsValid() || !z.GetBool(GenKey, false)) continue;
                        if (clan != "all" && !string.Equals(z.GetString(ClanKey, ""), clan, StringComparison.OrdinalIgnoreCase)) continue;
                        ZDOMan.instance.DestroyZDO(z);
                        removed++;
                    }
                    if (clan == "all") GetState().pending.Clear();
                    SaveState();
                    Say($"rp_pop clear: {removed} generated citizen(s) removed (named NPCs untouched)");
                    break;
                }

                default:
                {
                    Census c = TakeCensus(plugin);
                    PopWorldState ws = GetState();
                    double left = ws.lastTick + db.population.tickSeconds - ZNet.instance.GetTimeSeconds();
                    plugin.Log.LogInfo($"Population: enabled={db.population.enabled}, generated={c.total}/" +
                        $"{db.population.maxGeneratedTotal}, groups={c.groups.Count}, pending={ws.pending.Values.Sum()}, " +
                        $"next tick in {Mathf.Max(0f, (float)left):0}s");
                    foreach (KeyValuePair<string, int> kv in c.byFaction)
                    {
                        FactionPopulation fp;
                        db.population.factions.TryGetValue(kv.Key, out fp);
                        plugin.Log.LogInfo($"  {kv.Key}: {kv.Value}/{(fp != null ? fp.maxPopulation : 0)} " +
                            $"(target {(fp != null ? fp.targetPopulation : 0)}, rate {(fp != null ? fp.reproductionRate : 0f)})");
                    }
                    foreach (KeyValuePair<string, int> kv in c.byClan)
                        plugin.Log.LogInfo($"    {kv.Key}: {kv.Value}");
                    Say($"Population: {c.total} generated, {c.groups.Count} groups (see log)");
                    break;
                }
            }
        }
        internal static void GroupsCommand(string[] args)
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null || plugin.Database == null || ZDOMan.instance == null) return;
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            Census c = TakeCensus(plugin);
            if (sub == "disband")
            {
                string clan = args.Length > 2 ? args[2] : "all";
                int n = 0;
                foreach (NpcGroup g in c.groups.Values)
                {
                    if (clan != "all" && !string.Equals(g.clan, clan, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (ZDO z in g.members)
                    {
                        z.SetOwner(ZDOMan.GetSessionID());
                        z.Set(GroupIdKey, "");
                        z.Set(GroupRoleKey, "");
                        z.Set(GroupLeaderKey, "");
                        n++;
                    }
                }
                Say($"rp_groups disband: {n} member(s) released");
                return;
            }
            foreach (NpcGroup g in c.groups.Values)
                plugin.Log.LogInfo($"{g.id} [{g.clan}] leader={g.leaderId} members={g.members.Count}");
            Say($"{c.groups.Count} group(s) (see log)");
        }
    }
    // ------------------------------------------------------------------ wandering component
    internal sealed class NpcWanderer : MonoBehaviour
    {
        private static readonly List<NpcWanderer> All = new List<NpcWanderer>();
        private ZNetView _view;
        private string _id;
        private ManagerNPCRP.NpcDefinition _npc;
        private MonsterAI _ai;
        private float _next;
        private int _leaderMisses;
        private static readonly FieldInfo RandomRange = typeof(BaseAI).GetField("m_randomMoveRange");
        private void OnEnable() { All.Add(this); }
        private void OnDisable() { All.Remove(this); }
        internal void Init(ZNetView view, string id, ManagerNPCRP.NpcDefinition npc)
        {
            _view = view;
            _id = id;
            _npc = npc;
            _ai = GetComponent<MonsterAI>();
            _next = Time.time + UnityEngine.Random.Range(5f, 20f);
            if (_ai != null && RandomRange != null)
                RandomRange.SetValue(_ai, 6f);   // keeps idle jitter small around the patrol point
        }
        private void Update()
        {
            if (_view == null || !_view.IsValid() || !_view.IsOwner() || _ai == null) return;
            if (Time.time < _next) return;
            ManagerNPCRP.NpcDatabase db = ManagerNPCRP.Instance != null ? ManagerNPCRP.Instance.Database : null;
            if (db == null || db.groupBehavior == null) return;
            GroupBehavior gb = db.groupBehavior;
            _next = Time.time + UnityEngine.Random.Range(gb.wanderMinSeconds, gb.wanderMaxSeconds);
            CityZone zone;
            if (!db.cityZones.TryGetValue(_npc.clan ?? "", out zone)) return;
            ZDO zdo = _view.GetZDO();
            if (zdo == null) return;
            string role = zdo.GetString(PopulationManager.GroupRoleKey, "");
            if (role == "follower")
            {
                NpcWanderer leader = FindLeader(zdo.GetString(PopulationManager.GroupLeaderKey, ""));
                if (leader == null)
                {
                    if (++_leaderMisses >= 6)   // leader dead or gone: become a solo wanderer
                    {
                        zdo.Set(PopulationManager.GroupRoleKey, "");
                        zdo.Set(PopulationManager.GroupIdKey, "");
                        zdo.Set(PopulationManager.GroupLeaderKey, "");
                    }
                    return;
                }
                _leaderMisses = 0;
                ZDO lz = leader._view.GetZDO();
                Vector3 center = new Vector3(lz.GetFloat(PopulationManager.TargetXKey, leader.transform.position.x), 0f,
                                             lz.GetFloat(PopulationManager.TargetZKey, leader.transform.position.z));
                float a = (Mathf.Abs(_id.GetHashCode()) % 360) * Mathf.Deg2Rad;
                SetTarget(center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * gb.followRadius, zdo);
                return;
            }
            float ang = UnityEngine.Random.value * Mathf.PI * 2f;
            float r = Mathf.Sqrt(UnityEngine.Random.value) * zone.wanderRadius;
            Vector3 target = new Vector3(zone.x + Mathf.Cos(ang) * r, 0f, zone.z + Mathf.Sin(ang) * r);
            if (role == "leader")
            {
                zdo.Set(PopulationManager.TargetXKey, target.x);
                zdo.Set(PopulationManager.TargetZKey, target.z);
            }
            SetTarget(target, zdo);
        }
        private static NpcWanderer FindLeader(string leaderId)
        {
            if (string.IsNullOrEmpty(leaderId)) return null;
            for (int i = 0; i < All.Count; i++)
                if (All[i] != null && All[i]._id == leaderId && All[i]._view != null && All[i]._view.IsValid())
                    return All[i];
            return null;
        }
        private void SetTarget(Vector3 p, ZDO zdo)
        {
            // Real walkable spot when the zone is loaded; otherwise the height estimate.
            PlacementResult r = NpcPlacement.Solve(p, new PlacementSettings { maxSearchRadius = 8f }, null, transform, true);
            Vector3 point = r.found ? r.position : new Vector3(p.x, ManagerNPCRP.ResolveGroundHeight(p), p.z);
            // BaseAI.SetPatrolPoint(Vector3) when available, else write the ZDO fields the AI reads.
            MethodInfo m = typeof(BaseAI).GetMethod("SetPatrolPoint", new[] { typeof(Vector3) });
            if (m != null)
            {
                m.Invoke(_ai, new object[] { point });
            }
            else
            {
                zdo.Set(StringExtensionMethods.GetStableHashCode("patrolPoint"), point);
                zdo.Set(StringExtensionMethods.GetStableHashCode("patrol"), true);
            }
        }
    }
}
