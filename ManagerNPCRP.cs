using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Norsemen;
using UnityEngine;

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
        private const int SupportedSchemaVersion = 2;

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
        private readonly HashSet<string> _livingRoutineWarnings =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            MethodInfo vikingStart = AccessTools.Method(typeof(Viking), "Start");
            if (vikingStart != null)
            {
                _harmony.Patch(
                    vikingStart,
                    postfix: new HarmonyMethod(typeof(ManagerNPCRP), nameof(VikingStartPostfix)));
            }

            MethodInfo queueSay = AccessTools.Method(typeof(Viking), "QueueSay");
            if (queueSay != null)
            {
                _harmony.Patch(
                    queueSay,
                    prefix: new HarmonyMethod(typeof(ManagerNPCRP), nameof(VikingQueueSayPrefix)));
            }

            MethodInfo say = AccessTools.Method(typeof(Viking), "Say");
            if (say != null)
            {
                _harmony.Patch(
                    say,
                    prefix: new HarmonyMethod(typeof(ManagerNPCRP), nameof(VikingSayPrefix)));
            }

            MethodInfo zoneStart = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (zoneStart != null)
            {
                _harmony.Patch(
                    zoneStart,
                    postfix: new HarmonyMethod(typeof(ManagerNPCRP), nameof(ZoneSystemStartPostfix)));
            }

            RegisterCommands();

            _log.LogInfo($"{PluginName} {PluginVersion} loaded.");
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

            int created = 0;
            HashSet<string> existing = force ? new HashSet<string>() : CollectExistingNpcIds();

            foreach (KeyValuePair<string, NpcDefinition> pair in _npcs)
            {
                string id = pair.Key;
                NpcDefinition npc = pair.Value;

                if (npc.locations == null || npc.locations.spawn == null ||
                    !npc.locations.spawn.enabled)
                    continue;

                if (!force && _database.settings.preventDuplicates && existing.Contains(id))
                    continue;

                if (SpawnNpcZdo(id, npc))
                    created++;
            }

            _log.LogInfo($"spawn_rp_npcs: {created} RP NPC ZDO(s) created.");
            return created;
        }

        private bool SpawnNpcZdo(string id, NpcDefinition npc)
        {
            SpawnPosition p = npc.locations.spawn;

            int prefabHash = StringExtensionMethods.GetStableHashCode(npc.prefab);
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);

            if (prefab == null)
            {
                _log.LogError($"[{id}] Prefab not found in ZNetScene: {npc.prefab}");
                return false;
            }

            ZNetView prefabView = prefab.GetComponent<ZNetView>();
            if (prefabView == null)
            {
                _log.LogError($"[{id}] Prefab '{npc.prefab}' has no ZNetView.");
                return false;
            }

            Vector3 position = new Vector3(p.x, p.y, p.z);
            if (_database.settings.snapToGround)
                position.y = ResolveGroundHeight(position) + _database.settings.spawnHeightOffset;
            Vector3 rotation = new Vector3(0f, p.rotationY, 0f);

            ZDO zdo = ZDOMan.instance.CreateNewZDO(position, prefabHash);
            if (zdo == null)
            {
                _log.LogError($"[{id}] CreateNewZDO returned null.");
                return false;
            }

            zdo.m_prefab = prefabHash;
            zdo.m_rotation = rotation;
            zdo.Persistent = prefabView.m_persistent;
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
                $"position={position}, persistent={zdo.Persistent}");

            return true;
        }

        private float PointY(SpawnPosition p)
        {
            if (_database != null && _database.settings.snapToGround)
                return ResolveGroundHeight(new Vector3(p.x, p.y, p.z)) +
                       _database.settings.spawnHeightOffset;
            return p.y;
        }

        private void ApplyLivingNorsemenZdoData(ZDO zdo, NpcDefinition npc)
        {
            if (npc.locations == null)
                return;

            // Static NPCs stay at their post: no LivingNorsemen routine is consumed.
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

        private void SyncLivingNorsemenRuntime(Viking viking, NpcDefinition npc)
        {
            if (viking == null || npc == null || npc.locations == null ||
                (npc.behavior != null && npc.behavior.isStatic))
                return;

            ZNetView nview = viking.GetComponent<ZNetView>();
            if (nview == null || !nview.IsOwner())
                return;

            try
            {
                Type livingType = Type.GetType(
                    "LivingNorsemen.LivingNorseman, LivingNorsemen",
                    false);

                if (livingType == null)
                {
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            livingType = assembly.GetType("LivingNorsemen.LivingNorseman", false);
                        }
                        catch
                        {
                            // Ignore assemblies that cannot expose the type.
                        }

                        if (livingType != null)
                            break;
                    }
                }

                if (livingType == null)
                    return; // LivingNorsemen is optional.

                Component living = viking.GetComponent(livingType);
                if (living == null)
                    return;

                InvokeLivingRoutineMethod(livingType, living, "AssignHome",
                    npc.locations.home);
                InvokeLivingRoutineMethod(livingType, living, "AssignWork",
                    npc.locations.work);
                InvokeLivingRoutineMethod(livingType, living, "AssignPatrolPointB",
                    npc.locations.patrolPointB);
            }
            catch (Exception ex)
            {
                string id = npc.identity != null ? npc.identity.name : "unknown";
                if (_livingRoutineWarnings.Add(id))
                    _log.LogWarning(
                        $"[{id}] LivingNorsemen runtime sync failed: {ex.Message}");
            }
        }

        private void InvokeLivingRoutineMethod(
            Type livingType,
            Component living,
            string methodName,
            SpawnPosition point)
        {
            if (point == null || !point.enabled)
                return;

            MethodInfo method = AccessTools.Method(
                livingType,
                methodName,
                new[] { typeof(Vector3) });

            if (method == null)
            {
                string key = livingType.FullName + "." + methodName;
                if (_livingRoutineWarnings.Add(key))
                    _log.LogWarning(
                        $"LivingNorsemen API method '{methodName}(Vector3)' was not found.");
                return;
            }

            method.Invoke(living, new object[]
            {
                new Vector3(point.x, PointY(point), point.z)
            });
        }

        private System.Collections.IEnumerator ReapplyLivingNorsemenLater(
            Viking viking,
            NpcDefinition npc)
        {
            yield return new WaitForSeconds(0.25f);
            if (viking != null)
                SyncLivingNorsemenRuntime(viking, npc);
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

        private HashSet<string> CollectExistingNpcIds()
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ZDOMan.instance == null)
                return ids;

            foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
            {
                if (zdo == null || !zdo.Valid)
                    continue;
                string id = zdo.GetString(RpIdKey, "");
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
            return ids;
        }

        // Zone may not be loaded on the server: fall back to the world generator.
        private static float ResolveGroundHeight(Vector3 p)
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

            foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
            {
                if (zdo == null || !zdo.Valid)
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
                Instance.StartCoroutine(Instance.AutoSpawnRoutine());
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
                if (!_npcs.TryGetValue(id, out npc))
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
                }

                // LivingNorseman caches Home/Work/Patrol during its own Awake.
                // Sync the public runtime API again after our Viking hook so the
                // already-created component consumes the RP points immediately.
                SyncLivingNorsemenRuntime(viking, npc);
                StartCoroutine(ReapplyLivingNorsemenLater(viking, npc));

                // m_faction is a local field (not synced by ZDO): every client must set it.
                ApplyFaction(viking, npc);

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

        private static void VikingStartPostfix(Viking __instance)
        {
            if (Instance == null || __instance == null)
                return;

            // Norseman.Create assigns its configured faction before the Viking
            // component exists. Re-apply at Start as a second ordering barrier
            // against other Awake/initialization patches.
            Instance.ApplyFactionFromZdo(__instance);
        }

        private static void VikingQueueSayPrefix(
            Viking __instance,
            ref List<string> texts)
        {
            if (Instance != null)
                Instance.OverrideDialogueList(__instance, ref texts);
        }

        private static void VikingSayPrefix(
            Viking __instance,
            ref List<string> texts)
        {
            if (Instance != null)
                Instance.OverrideDialogueList(__instance, ref texts);
        }

        private void ApplyFactionFromZdo(Viking viking)
        {
            if (viking == null)
                return;

            ZNetView nview = viking.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null)
                return;

            string id = nview.GetZDO().GetString(RpIdKey, "");
            NpcDefinition npc;
            if (!string.IsNullOrWhiteSpace(id) && _npcs.TryGetValue(id, out npc))
                ApplyFaction(viking, npc);
        }

        private void OverrideDialogueList(Viking viking, ref List<string> texts)
        {
            if (_database == null || viking == null || texts == null)
                return;

            ZNetView nview = viking.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null)
                return;

            string id = nview.GetZDO().GetString(RpIdKey, "");
            NpcDefinition npc;
            if (string.IsNullOrWhiteSpace(id) || !_npcs.TryGetValue(id, out npc))
                return;

            DialogueSetDefinition set = ResolveDialogueSet(npc);
            if (set == null)
                return;

            // This is the real Norsemen contract: Viking.QueueSay/Say receive
            // the exact List<string> returned by TalkManager.GetTalk(TalkType).
            // Replace that list only for RP Vikings, without touching Norsemen's
            // global TalkManager configuration.
            if (ReferenceEquals(texts, TalkManager.GetTalk(TalkManager.TalkType.Greets)))
                texts = set.Greets;
            else if (ReferenceEquals(texts, TalkManager.GetTalk(TalkManager.TalkType.Farewells)))
                texts = set.Farewells;
            else if (ReferenceEquals(texts, TalkManager.GetTalk(TalkManager.TalkType.Generic)))
                texts = set.Generic;
            else if (ReferenceEquals(texts, TalkManager.GetTalk(TalkManager.TalkType.PlayerBase)))
                texts = set.PlayerBase;
            else if (ReferenceEquals(texts, TalkManager.GetTalk(TalkManager.TalkType.Damaged)))
                texts = set.Damaged;
            else if (ReferenceEquals(texts, TalkManager.GetTalk(TalkManager.TalkType.Thieved)))
                texts = set.Thieved;
            else if (ReferenceEquals(texts, TalkManager.GetTalk(TalkManager.TalkType.Puke)))
                texts = set.Puke;
            else if (ReferenceEquals(texts, TalkManager.GetTalk(TalkManager.TalkType.Eat)))
                texts = set.Eat;
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
            if (npc.behavior != null && npc.behavior.aggressive &&
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
                return;

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
        // DIALOGUES
        // ---------------------------------------------------------------------

        private DialogueSetDefinition ResolveDialogueSet(NpcDefinition npc)
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
                    string id = args.Length > 1 ? args[1] : "";
                    string kind = args.Length > 2 ? args[2].ToLowerInvariant() : "";

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
                    string id = args.Length > 1 ? args[1] : "";
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
                "rp_npc_info",
                "Show one RP NPC: rp_npc_info NPC-001",
                args =>
                {
                    string id = args.Length > 1 ? args[1] : "";
                    ShowNpcInfo(id);
                    return true;
                },
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
                $"worldZdo={(zdo != null ? zdo.GetZDOID().ToString() : "NOT FOUND")}");
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
            public bool respawnIfDead;
            public bool debug;
            public bool snapToGround = true;
            public float spawnHeightOffset = 0.5f;
            public string defaultPlayerFaction;
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
        }

        [Serializable]
        public sealed class NpcDefinition
        {
            public IdentityDefinition identity = new IdentityDefinition();
            public string prefab;
            public string clan;
            public string faction;
            public string role;
            public LivingNorsemenDefinition livingNorsemen =
                new LivingNorsemenDefinition();
            public string equipmentSet;
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
}
