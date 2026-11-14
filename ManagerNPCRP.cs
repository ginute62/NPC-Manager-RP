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
        public const string PluginVersion = "0.2.0";
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
        private ConfigEntry<KeyCode> _questKey;
        internal KeyCode QuestKeyValue { get { return _questKey != null ? _questKey.Value : KeyCode.P; } }
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
            _questKey = Config.Bind("Interaction", "QuestKey", KeyCode.P,
                "Key to open the quest window of the nearest RP NPC (accept / turn in quests). Without an NPC in range it opens the quest journal.");
            Pvp.Init(Config);
            try
            {
                MethodInfo onDeath = AccessTools.Method(typeof(Character), "OnDeath");
                MethodInfo rpcDamage = AccessTools.Method(typeof(Character), "RPC_Damage");
                if (rpcDamage != null)
                    _harmony.Patch(rpcDamage, prefix: new HarmonyMethod(typeof(ReputationManager), nameof(ReputationManager.DamagePrefix)));
                else Log.LogWarning("Character.RPC_Damage not found: same-allegiance friendly-fire protection is inactive.");
                if (onDeath != null)
                    _harmony.Patch(onDeath, postfix: new HarmonyMethod(typeof(ReputationManager), nameof(ReputationManager.PlayerDeathPostfix)));
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
            try
            {
                MethodInfo isEnemy = AccessTools.Method(typeof(BaseAI), "IsEnemy",
                    new[] { typeof(Character), typeof(Character) });
                if (isEnemy != null)
                {
                    _harmony.Patch(isEnemy, postfix: new HarmonyMethod(typeof(ReputationManager),
                        nameof(ReputationManager.IsEnemyPostfix)));
                    ReputationManager.EnemyPatchActive = true;
                }
                else
                    _log.LogWarning("BaseAI.IsEnemy not found: career hostility falls back to the global hostile faction variant.");
            }
            catch (Exception ex)
            {
                _log.LogWarning("IsEnemy patch unavailable: " + ex.Message);
            }

            try
            {
                MethodInfo qDeath = AccessTools.Method(typeof(Character), "OnDeath");
                if (qDeath != null)
                    _harmony.Patch(qDeath, postfix: new HarmonyMethod(typeof(QuestEngine), nameof(QuestEngine.OnDeathPostfix)));
                MethodInfo qGame = AccessTools.Method(typeof(Game), "Start");
                if (qGame != null)
                    _harmony.Patch(qGame, postfix: new HarmonyMethod(typeof(QuestEngine), nameof(QuestEngine.GameStartPostfix)));
                // the quest window frees the cursor and freezes player input while open
                MethodInfo takeInput = AccessTools.Method(typeof(Player), "TakeInput");
                if (takeInput != null)
                    _harmony.Patch(takeInput, postfix: new HarmonyMethod(typeof(QuestUi), nameof(QuestUi.TakeInputPostfix)));
                else _log.LogWarning("Player.TakeInput not found: the quest window will not freeze player input.");
                MethodInfo mouseCap = AccessTools.Method(typeof(GameCamera), "UpdateMouseCapture");
                if (mouseCap != null)
                    _harmony.Patch(mouseCap, prefix: new HarmonyMethod(typeof(QuestUi), nameof(QuestUi.MouseCapturePrefix)));
                else _log.LogWarning("GameCamera.UpdateMouseCapture not found: the cursor may be recaptured while the quest window is open.");

                // Diagnostic: list input-related ZInput methods available in this game build.
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(ZInput)))
                {
                    string name = method.Name;

                    if (name.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Mouse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Key", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _log.LogInfo("ZInput input method: " + name);
                    }
                }

                // Guaranteed gameplay-input block: runs immediately before Player.Update.
                MethodInfo playerUpdate = AccessTools.Method(typeof(Player), "Update");

                if (playerUpdate != null)
                {
                    _harmony.Patch(
                        playerUpdate,
                        prefix: new HarmonyMethod(typeof(QuestUi), nameof(QuestUi.PlayerUpdatePrefix)));
                }
                else
                {
                    _log.LogWarning("Player.Update not found: quest UI cannot block attacks reliably.");
                }

                MethodInfo zInputButton = AccessTools.Method(typeof(ZInput), "GetButton");
                if (zInputButton != null)
                {
                    _harmony.Patch(
                        zInputButton,
                        prefix: new HarmonyMethod(typeof(QuestUi), nameof(QuestUi.GetButtonPrefix)));
                }

                MethodInfo zInputButtonDown = AccessTools.Method(typeof(ZInput), "GetButtonDown");
                if (zInputButtonDown != null)
                {
                    _harmony.Patch(
                        zInputButtonDown,
                        prefix: new HarmonyMethod(typeof(QuestUi), nameof(QuestUi.GetButtonDownPrefix)));
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Quest patches partly unavailable: " + ex.Message);
            }

            RegisterCommands();
            QuestEngine.RegisterCommands();
            _log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }
        private void Update()
        {
            // T = existing dialogue, O = quest window. One interaction per frame, none while the quest window is open.
            bool talked = false;
            if (_talkKey != null && !QuestUi.IsOpen && Input.GetKeyDown(_talkKey.Value))
            {
                NpcTalk.TryTalk();
                talked = true;
            }
            QuestEngine.Update(talked);
            ReputationManager.ClientTick();
        }
        private void OnGUI()
        {
            try { ReputationManager.DrawPvpLabels(); } catch { }
            if (!QuestUi.IsOpen) return;
            GUI.skin.label.richText = true;
            GUI.skin.label.wordWrap = true;
            QuestUi.OnGui();
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
                // Career ids are matched case-insensitively everywhere.
                database.careers = database.careers == null
                    ? new Dictionary<string, CareerDefinition>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, CareerDefinition>(database.careers, StringComparer.OrdinalIgnoreCase);
                if (database.reputation != null && database.reputation.repFactions != null)
                    database.reputation.repFactions =
                        database.reputation.repFactions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (database.schemaVersion == 2)
                {
                    PopulationManager.MigrateToV3(database);
                    database.schemaVersion = 3;
                    migrated = true;
                }
                _database = database;
                ReputationManager.ClearNpcCache();
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
                _log.LogInfo("Careers loaded: " + (database.careers.Count == 0
                    ? "NONE (section 'careers' missing or empty in the JSON!)"
                    : string.Join(", ", database.careers.Keys.ToArray())));
                CareerManager.Validate(this);
                QuestEngine.Load(this);
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
                {
                    _log.LogWarning(
                        $"Prefab not found in ZNetScene: {npc.prefab} " +
                        $"(first seen on [{id}]; same prefab = same failure)."
                    );
                }

                // Do not let one unavailable biome prefab block every RP NPC.
                const string fallbackPrefabName = "Meadows_Norseman_RS";
                int fallbackHash = StringExtensionMethods.GetStableHashCode(fallbackPrefabName);
                prefab = ZNetScene.instance.GetPrefab(fallbackHash);

                if (prefab == null)
                {
                    _log.LogError(
                        $"Fallback prefab '{fallbackPrefabName}' is also unavailable. " +
                        "VikingNPC / Norsemen may not be loaded or its prefabs are not registered yet."
                    );

                    return false;
                }

                prefabHash = fallbackHash;
                _log.LogWarning($"[{id}] Using fallback prefab '{fallbackPrefabName}'.");
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
            if (ai == null) { zdo.Set(key, true); return; }
            try
            {
                ai.SetPatrolPoint(); // anchors the NPC at its current (spawn) position
                zdo.Set(key, true);
            }
            catch (NullReferenceException)
            {
                // The AI is not initialized yet (BaseAI wires its ZNetView in Awake, which may run after this hook):
                // retry a few times instead of aborting the whole RP data application (name, role, dialogue...).
                StartCoroutine(AnchorWhenReady(viking, zdo, key));
            }
        }
        private System.Collections.IEnumerator AnchorWhenReady(Viking viking, ZDO zdo, string key)
        {
            for (int i = 0; i < 120; i++)
            {
                yield return new WaitForSeconds(0.5f);
                if (viking == null) yield break;
                MonsterAI ai = viking.GetComponent<MonsterAI>();
                if (ai == null) continue;
                bool ok = false;
                try { ai.SetPatrolPoint(); ok = true; }
                catch (NullReferenceException) { }
                if (ok) { zdo.Set(key, true); yield break; }
            }
            Log.LogWarning("Static NPC could not be anchored (AI never became ready): " + (viking != null ? viking.name : "?"));
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
            if (Instance == null)
                return;
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

            // VikingNPC / Norsemen may register its custom prefabs after ZNetScene becomes available.
            yield return WaitForNorsemanPrefabs();

            _autoSpawnDone = true;
            SpawnAllConfiguredNPCs(false);
        }

        private static readonly string[] NorsemanPrefabNames =
        {
            "Meadows_Norseman_RS",
            "BlackForest_Norseman_RS",
            "Swamp_Norseman_RS",
            "Mountains_Norseman_RS",
            "Plains_Norseman_RS",
            "Mistlands_Norseman_RS",
            "AshLands_Norseman_RS"
        };

        private static bool IsNorsemanPrefabAvailable(string prefabName)
        {
            if (ZNetScene.instance == null || string.IsNullOrWhiteSpace(prefabName))
                return false;

            int hash = StringExtensionMethods.GetStableHashCode(prefabName);
            return ZNetScene.instance.GetPrefab(hash) != null;
        }

        private static bool AreNorsemanPrefabsReady()
        {
            if (ZNetScene.instance == null)
                return false;

            foreach (string prefabName in NorsemanPrefabNames)
            {
                if (!IsNorsemanPrefabAvailable(prefabName))
                    return false;
            }

            return true;
        }

        private System.Collections.IEnumerator WaitForNorsemanPrefabs()
        {
            const float timeoutSeconds = 180f;
            float elapsed = 0f;

            while (elapsed < timeoutSeconds)
            {
                if (AreNorsemanPrefabsReady())
                {
                    _log.LogInfo("Norseman prefabs are ready.");
                    yield break;
                }

                yield return new WaitForSeconds(1f);
                elapsed += 1f;
            }

            List<string> missing = new List<string>();
            foreach (string prefabName in NorsemanPrefabNames)
            {
                if (!IsNorsemanPrefabAvailable(prefabName))
                    missing.Add(prefabName);
            }

            _log.LogWarning(
                "Norseman prefabs were not registered after " + timeoutSeconds + " seconds. " +
                "Missing: " + string.Join(", ", missing) +
                ". VikingNPC / Norsemen may be missing, outdated, or failed to initialize."
            );
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
                ApplyGuardBehaviour(viking, npc);
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
        private bool _guardApiLogged;
        // Guard + reputation OK  -> Guard/Passive (patrouille tranquille)
        // Guard + faction hostile -> Guard/Aggressive (cherche des cibles)
        // Reflection on purpose: VikingAI.SetMovement/SetEmotion are not part of a public API.
        private void ApplyGuardBehaviour(Viking viking, NpcDefinition npc)
        {
            if (viking == null || npc == null)
                return;
            bool isGuard =
                string.Equals(npc.role, "Guard", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(npc.role, "Soldier", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(npc.role, "Watchman", StringComparison.OrdinalIgnoreCase) ||
                (npc.livingNorsemen != null &&
                 string.Equals(npc.livingNorsemen.profession, "Guard", StringComparison.OrdinalIgnoreCase));
            if (!isGuard)
                return;

            Component ai = null;
            foreach (Component c in viking.GetComponents<Component>())
            {
                if (c != null && c.GetType().Name == "VikingAI") { ai = c; break; }
            }
            if (ai == null)
                return;

            bool hostile = ReputationManager.GuardsHostile(npc);
            bool m = InvokeEnumSetter(ai, "SetMovement", "Guard");
            bool rounds = Pvp.GuardRounds != null && Pvp.GuardRounds.Value && npc.behavior != null && !npc.behavior.isStatic &&
                          npc.locations != null && npc.locations.patrolPointB != null && npc.locations.patrolPointB.enabled;
            bool e = InvokeEnumSetter(ai, "SetEmotion", (hostile || rounds) ? "Aggressive" : "Passive");

            if (!_guardApiLogged && _database != null && _database.settings.debug)
            {
                _guardApiLogged = true;
                _log.LogInfo($"ApplyGuardBehaviour: SetMovement={(m ? "ok" : "not found")}, " +
                             $"SetEmotion={(e ? "ok" : "not found")} on {ai.GetType().FullName}");
            }
        }

        private static bool InvokeEnumSetter(object target, string method, string valueName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                // The enum type is read from the AI's own field (m_moveType / m_behaviour): no assembly scan (GetTypes() can throw on mod assemblies).
                string field = method == "SetMovement" ? "m_moveType" : method == "SetEmotion" ? "m_behaviour" : null;
                FieldInfo fi = field != null ? target.GetType().GetField(field, flags) : null;
                Type et = fi != null && fi.FieldType.IsEnum ? fi.FieldType : null;
                if (et == null) return false;
                object val = Enum.Parse(et, valueName);
                foreach (MethodInfo mi in target.GetType().GetMethods(flags))
                {
                    if (mi.Name != method) continue;
                    ParameterInfo[] ps = mi.GetParameters();
                    if (ps.Length != 1) continue;
                    if (ps[0].ParameterType == typeof(int)) { mi.Invoke(target, new object[] { Convert.ToInt32(val) }); return true; }
                    if (ps[0].ParameterType == et) { mi.Invoke(target, new object[] { val }); return true; }
                }
            }
            catch { }
            return false;
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
            if (((npc.behavior != null && npc.behavior.aggressive) || (!ReputationManager.EnemyPatchActive && ReputationManager.GuardsHostile(npc))) &&
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
                ApplyGuardBehaviour(viking, npc);
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
        /// <summary>
        /// Norsemen passes the raw console tokens: ["norsemen", "rp_cmd", arg1, arg2...].
        /// Returns ["rp_cmd", arg1, arg2...] so that Args[0] = command, Args[1] = first parameter,
        /// whatever the prefix the console adds.
        /// </summary>
        internal static string[] CmdArgs(string[] raw, string cmd)
        {
            if (raw == null) return new string[] { cmd };
            for (int i = 0; i < raw.Length; i++)
            {
                if (string.Equals(raw[i], cmd, StringComparison.OrdinalIgnoreCase))
                {
                    string[] res = new string[raw.Length - i];
                    Array.Copy(raw, i, res, 0, res.Length);
                    return res;
                }
            }
            return raw;
        }

        private void RegisterCommands()
        {
            new NorseCommand(
                "rp_prefabs",
                "List Norseman prefabs available in ZNetScene.",
                args =>
                {
                    if (ZNetScene.instance == null)
                    {
                        _log.LogWarning("ZNetScene is not ready.");
                        return true;
                    }

                    foreach (string prefabName in NorsemanPrefabNames)
                    {
                        bool available = IsNorsemanPrefabAvailable(prefabName);
                        _log.LogInfo($"{prefabName}: {(available ? "AVAILABLE" : "MISSING")}");
                    }

                    return true;
                },
                adminOnly: true);
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
                    string id = CmdArgs(args.Args, "rp_set_point").Length > 1 ? CmdArgs(args.Args, "rp_set_point")[1] : "";
                    string kind = CmdArgs(args.Args, "rp_set_point").Length > 2 ? CmdArgs(args.Args, "rp_set_point")[2].ToLowerInvariant() : "";

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
                    string id = CmdArgs(args.Args, "rp_tp").Length > 1 ? CmdArgs(args.Args, "rp_tp")[1] : "";
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
                args => { NpcPlacement.ScanCommand(CmdArgs(args.Args, "rp_scan")); return true; },
                adminOnly: true);
            new NorseCommand(
                "rp_pop",
                "Population: rp_pop status|on|off|tick|spawn <Clan> <N>|clear [Clan|all]",
                args => { PopulationManager.PopCommand(CmdArgs(args.Args, "rp_pop")); return true; },
                adminOnly: true);
            new NorseCommand(
                "rp_groups",
                "Groups: rp_groups list | rp_groups disband [Clan|all]",
                args => { PopulationManager.GroupsCommand(CmdArgs(args.Args, "rp_groups")); return true; },
                adminOnly: true);
            new NorseCommand(
                "rp_audit",
                "Server audit of RP NPCs: missing, duplicated, orphaned, non-persistent. 'rp_audit fix' repairs.",
                args => { NpcLifecycle.AuditCommand(CmdArgs(args.Args, "rp_audit")); return true; },
                adminOnly: true);
            new NorseCommand(
                "rp_check",
                "Check loaded NPCs in the world: underground, in water, stuck, floating, drifted. rp_check [Clan|NPC-ID|all] [fix]",
                args => { NpcPlacement.CheckCommand(CmdArgs(args.Args, "rp_check")); return true; },
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
                args => { ReputationManager.CareerCommand(CmdArgs(args.Args, "rp_career")); return true; },
                adminOnly: false);
            new NorseCommand(
                "rp_company",
                "Companies (same allegiance): rp_company create <name> | invite <player> | accept | leave | kick <player> | disband | list",
                args => { ReputationManager.CompanyConsole(CmdArgs(args.Args, "rp_company")); return true; },
                adminOnly: false);
            new NorseCommand(
                "rp_playercareer",
                "Assign a career to a player: rp_playercareer <player> <career> (admin client or server).",
                args => { ReputationManager.BindCommandArgs(CmdArgs(args.Args, "rp_playercareer"), 1); return true; },
                adminOnly: true);
            new NorseCommand(
                "rp_enemies",
                "Show your career's enemy factions and how many loaded NPCs are hostile to you.",
                args => { ReputationManager.EnemiesCommand(); return true; },
                adminOnly: false);
            new NorseCommand(
                "rp_listcareer",
                "List players and their assigned career (admin client or server).",
                args => { ReputationManager.ListCareerCommand(); return true; },
                adminOnly: true);
            new NorseCommand(
                "rp_calendar",
                "Show this month's objectives for your career (360-day campaign).",
                args => { ReputationManager.CalendarCommand(); return true; },
                adminOnly: false);
            new NorseCommand(
                "rp_rep",
                "Reputation: status | month | add|set <Clan|Faction> <pts> [career] | grudge <NPC-ID> [career] | forgive <NPC-ID|all> [career]",
                args => { ReputationManager.RepCommand(CmdArgs(args.Args, "rp_rep")); return true; },
                adminOnly: true);
            new NorseCommand(
                "rp_journal",
                "Show the clues you have heard and your standing with each clan.",
                args => { NpcTalk.JournalCommand(); return true; },
                adminOnly: false);
            new NorseCommand(
                "rp_flag",
                "Story flags (world-wide): rp_flag list | rp_flag set <flag> | rp_flag clear <flag>",
                args => { DialogueStages.FlagCommand(CmdArgs(args.Args, "rp_flag")); return true; },
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
        public List<string> aliases = new List<string>();          // alternative names accepted by rp_career / rp_playercareer
        public List<string> enemyFactions = new List<string>();    // factions whose guards become hostile to this career
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
        public CareerHostilitySettings careerHostility = new CareerHostilitySettings();
    }
    [Serializable]
    public sealed class CareerHostilitySettings
    {
        public string _note;
        public bool enabled = true;
        public bool guardsOnly = true;    // false = every NPC of an enemy faction
    }
    public sealed class Ledger
    {
        public Dictionary<string, int> clanPoints = new Dictionary<string, int>();
        public Dictionary<string, int> clanNights = new Dictionary<string, int>();
        public Dictionary<string, int> clanNightPoints = new Dictionary<string, int>();
        public Dictionary<string, float> factionPoints = new Dictionary<string, float>();
    }
    /// <summary>A company: up to Companies.MaxMembers players of the same allegiance. Persistent, public to every player.</summary>
    public sealed class Company
    {
        public string id = "";
        public string name = "";
        public string career = "";
        public string leader = "";
        public List<string> members = new List<string>();
    }
    public sealed class RepWorld
    {
        public Dictionary<string, Company> companies = new Dictionary<string, Company>();
        public int companyCounter;
        public Dictionary<string, long> switchAt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);   // last allegiance change (UTC ticks)
        public Dictionary<string, int> pvpKills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> pvpDeaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
    internal sealed class RosterMember { public string name = ""; public bool online; public string company = ""; public int kills, deaths; }
    internal sealed class CompanyInfo
    {
        public string id = "", name = "", career = "", leader = "";
        public List<string> members = new List<string>();
    }
    /// <summary>What the client knows about its faction and the companies (pushed by the server).</summary>
    internal sealed class RosterSnap
    {
        public string career = "", companyId = "", companyName = "", inviteCompany = "", inviter = "";
        public int max = 3;
        public float at;
        public List<RosterMember> members = new List<RosterMember>();
        public List<CompanyInfo> companies = new List<CompanyInfo>();
    }
    internal static class Pvp
    {
        internal static ConfigEntry<bool> Enabled, FriendlyFire, ShowLabels, AllowSwitch, GuardRounds;
        internal static ConfigEntry<float> SwitchCooldownHours;
        internal static ConfigEntry<float> KillPoints, DeathPoints, CooldownSeconds;
        internal static ConfigEntry<int> KillCoins, CompanyMax;
        internal static void Init(ConfigFile c)
        {
            Enabled = c.Bind("PvP", "Rewards", true, "Killing a player of ANOTHER allegiance: reputation + coins for the killer's career, reputation loss for the victim's career.");
            KillPoints = c.Bind("PvP", "KillReputation", 1f, "Faction reputation gained by the killer's career per PvP kill.");
            DeathPoints = c.Bind("PvP", "DeathReputationLoss", 1f, "Faction reputation lost by the victim's career when killed by a foreign-allegiance player.");
            KillCoins = c.Bind("PvP", "KillCoins", 10, "Coins given to the killer.");
            CooldownSeconds = c.Bind("PvP", "PairCooldownSeconds", 300f, "Anti-farming: the same killer/victim pair pays only once per this delay.");
            FriendlyFire = c.Bind("PvP", "FriendlyFire", false, "false = two players of the same allegiance can never hurt each other, even with PvP on. true = vanilla behaviour.");
            ShowLabels = c.Bind("PvP", "FactionLabels", true, "Show [PvP] + faction (and company) above players who switched PvP on.");
            CompanyMax = c.Bind("Companies", "MaxMembers", 3, "Maximum players per company (same allegiance required).");
            AllowSwitch = c.Bind("Allegiance", "AllowSwitch", true, "Players may change allegiance from the Faction tab (they lose the reputation of the old faction: reputation belongs to the career).");
            SwitchCooldownHours = c.Bind("Allegiance", "SwitchCooldownHours", 24f, "Real hours between two allegiance changes of the same player (0 = no delay).");
            GuardRounds = c.Bind("Guards", "AggressiveOnRounds", true, "Guards that have a patrol point B are set to Guard/Aggressive so LivingNorseman starts their rounds (they stay friendly to players unless the faction is hostile).");
        }
    }
    // ============================================================
    // CAREER MANAGER
    // ============================================================
    internal static class CareerManager
    {
        public enum CareerResult
        {
            Ok,
            UnknownCareer,
            AlreadyChosen,
            UnknownPlayer,
            NotAdmin,
            InvalidTarget
        }

        public static void Validate(ManagerNPCRP plugin)
        {
            ManagerNPCRP.NpcDatabase db = plugin?.Database;
            if (db?.careers == null || db.reputation?.repFactions == null) return;

            HashSet<string> factions = new HashSet<string>(
                db.reputation.repFactions, StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, CareerDefinition> kv in db.careers)
            {
                string id = kv.Key;
                CareerDefinition career = kv.Value;

                if (career == null)
                {
                    plugin.Log.LogWarning("Career '" + id + "' is null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(career.faction) || !factions.Contains(career.faction))
                    plugin.Log.LogWarning("Career '" + id + "': unknown faction '" + career.faction + "'.");

                if (career.hubClans != null)
                {
                    foreach (string clan in career.hubClans)
                    {
                        if (string.IsNullOrWhiteSpace(clan)) continue;

                        if (!db.clans.ContainsKey(clan))
                            plugin.Log.LogWarning("Career '" + id + "': unknown hubClan '" + clan + "'.");

                        if (!db.cityZones.ContainsKey(clan) &&
                            !string.Equals(clan, "Itinerants", StringComparison.OrdinalIgnoreCase))
                        {
                            plugin.Log.LogWarning("Career '" + id + "': hubClan '" + clan + "' has no cityZone.");
                        }
                    }
                }

                if (career.enemyFactions != null)
                {
                    foreach (string ef in career.enemyFactions)
                    {
                        if (!factions.Contains(ef))
                            plugin.Log.LogWarning("Career '" + id + "': enemyFactions has unknown faction '" + ef + "'.");
                        else if (string.Equals(ef, career.faction, StringComparison.OrdinalIgnoreCase))
                            plugin.Log.LogWarning("Career '" + id + "': its own faction '" + ef + "' is listed as enemy (ignored).");
                    }
                }

                if (career.monthlyDrift != null)
                {
                    foreach (string faction in career.monthlyDrift.Keys)
                    {
                        if (!factions.Contains(faction))
                            plugin.Log.LogWarning("Career '" + id + "': monthlyDrift targets unknown faction '" + faction + "'.");
                    }
                }
            }
        }

        public static CareerResult Assign(
            ManagerNPCRP plugin,
            string targetPlayerName,
            string careerId,
            bool adminOverride)
        {
            ManagerNPCRP.NpcDatabase db = plugin?.Database;
            if (db?.careers == null) return CareerResult.UnknownCareer;

            careerId = ReputationManager.ResolveCareerKey(db, careerId) ?? string.Empty;
            if (!db.careers.ContainsKey(careerId)) return CareerResult.UnknownCareer;

            if (string.IsNullOrWhiteSpace(targetPlayerName)) return CareerResult.InvalidTarget;

            string resolved = ResolveOnlinePlayerName(targetPlayerName);
            if (resolved == null) return CareerResult.UnknownPlayer;

            RepWorld ws = ReputationManager.GetWorldState();
            string current = ws.playerCareer.TryGetValue(resolved, out string existing) ? existing : null;

            if (!string.IsNullOrEmpty(current) && !adminOverride)
                return CareerResult.AlreadyChosen;

            ws.playerCareer[resolved] = careerId;
            ReputationManager.SaveWorldState();

            return CareerResult.Ok;
        }

        /// <summary>Exact (case-insensitive) match, else a unique partial match, among online players.</summary>
        internal static bool TryResolveOnlinePlayer(string requested, out string name, out long uid)
        {
            name = null;
            uid = 0L;
            if (string.IsNullOrWhiteSpace(requested)) return false;

            List<KeyValuePair<string, long>> all = new List<KeyValuePair<string, long>>();
            if (Player.m_localPlayer != null && ZNet.instance != null)
                all.Add(new KeyValuePair<string, long>(Player.m_localPlayer.GetPlayerName(), ZNet.GetUID()));
            if (ZNet.instance != null)
                foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                    all.Add(new KeyValuePair<string, long>(peer.m_playerName, peer.m_uid));

            string wanted = requested.Trim();
            List<KeyValuePair<string, long>> hit = all
                .Where(x => string.Equals(x.Key, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 0)
                hit = all.Where(x => x.Key != null &&
                    x.Key.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hit.Count != 1) return false;

            name = hit[0].Key;
            uid = hit[0].Value;
            return true;
        }

        private static string ResolveOnlinePlayerName(string requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return null;

            string wanted = requested.Trim();
            List<string> matches = new List<string>();

            if (Player.m_localPlayer != null)
            {
                string localName = Player.m_localPlayer.GetPlayerName();
                if (string.Equals(localName, wanted, StringComparison.OrdinalIgnoreCase))
                    matches.Add(localName);
            }

            if (ZNet.instance != null)
            {
                foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                {
                    if (string.Equals(peer.m_playerName, wanted, StringComparison.OrdinalIgnoreCase))
                        matches.Add(peer.m_playerName);
                }
            }

            return matches.Count == 1 ? matches[0] : null;
        }

        public static string ResolveSenderName(long sender, string claimedName)
        {
            if (ZNet.instance == null) return null;

            if (sender == ZNet.GetUID() && Player.m_localPlayer != null)
            {
                string localName = Player.m_localPlayer.GetPlayerName();
                return string.Equals(localName, claimedName, StringComparison.OrdinalIgnoreCase)
                    ? localName
                    : null;
            }

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer.m_uid != sender) continue;

                return string.Equals(peer.m_playerName, claimedName, StringComparison.OrdinalIgnoreCase)
                    ? peer.m_playerName
                    : null;
            }

            return null;
        }

        public static bool IsSenderAdmin(long sender)
        {
            if (ZNet.instance == null) return false;

            if (sender == ZNet.GetUID()) return true;

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer.m_uid != sender) continue;
                return ZNet.instance.IsAdmin(peer.m_socket.GetHostName());
            }

            return false;
        }
    }

    /// <summary>Exact reputation points of the local career, pushed by the server (the client otherwise only sees tier flags).</summary>
    internal sealed class RepSnapshot
    {
        public string career = "";
        public float at;
        public Dictionary<string, float> clans = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, float> factions = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    }

    internal static class ReputationManager
    {
        // ---- exact points for the reputation tab (server -> client)
        internal static RepSnapshot Snapshot;
        private static float _lastSnapReq = -999f;

        private static string BuildSnapshotPayload(string career)
        {
            System.Globalization.CultureInfo ci = System.Globalization.CultureInfo.InvariantCulture;
            Ledger l = Led(Ws(), career);
            List<string> c = new List<string>(), f = new List<string>();
            foreach (KeyValuePair<string, int> kv in l.clanPoints) c.Add(kv.Key + "=" + kv.Value.ToString(ci));
            foreach (KeyValuePair<string, float> kv in l.factionPoints) f.Add(kv.Key + "=" + kv.Value.ToString("0.0", ci));
            return string.Join(",", c.ToArray()) + "|" + string.Join(",", f.ToArray());
        }

        private static void ParsePairs(string src, Dictionary<string, float> into)
        {
            if (string.IsNullOrEmpty(src)) return;
            foreach (string part in src.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                float v;
                if (eq > 0 && float.TryParse(part.Substring(eq + 1), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out v))
                    into[part.Substring(0, eq)] = v;
            }
        }

        private static void ApplySnapshot(string career, string payload)
        {
            RepSnapshot s = new RepSnapshot();
            s.career = career ?? ""; s.at = Time.time;
            string[] halves = (payload ?? "").Split('|');
            if (halves.Length > 0) ParsePairs(halves[0], s.clans);
            if (halves.Length > 1) ParsePairs(halves[1], s.factions);
            Snapshot = s;
        }

        /// <summary>Asks the server for the exact points of the local career (host computes it directly). Throttled unless forced.</summary>
        internal static void RequestSnapshot(bool force)
        {
            if (!force && Time.time - _lastSnapReq < 15f) return;
            _lastSnapReq = Time.time;
            string career = LocalCareer();
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                if (Db != null) ApplySnapshot(career, BuildSnapshotPayload(career));
                return;
            }
            if (ZRoutedRpc.instance != null)
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcAccess.ServerPeerId(), "RP_RepReq", career);
        }

        private static void RpcRepReq(long sender, string career)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || Db == null || ZRoutedRpc.instance == null) return;
            string key = ResolveCareerKey(Db, career);
            if (key == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "RP_RepData", key, BuildSnapshotPayload(key));
        }

        private static void RpcRepData(long sender, string career, string payload)
        {
            if (!string.Equals(career, LocalCareer(), StringComparison.OrdinalIgnoreCase)) return;
            ApplySnapshot(career, payload);
        }

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

        internal static bool HasChosenCareer()
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
        internal static RepWorld GetWorldState()
        {
            return Ws();
        }

        internal static void SaveWorldState()
        {
            Save();
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
            ZRoutedRpc.instance.Register<string, string>("RP_Bind", RpcBind);
            ZRoutedRpc.instance.Register<string>("RP_SetCareer", RpcSetCareer);
            ZRoutedRpc.instance.Register<string>("RP_Msg", RpcMsg);
            ZRoutedRpc.instance.Register<string>("RP_List", RpcList);
            ZRoutedRpc.instance.Register<string>("RP_Lines", RpcLines);
            ZRoutedRpc.instance.Register<string>("RP_RepReq", RpcRepReq);
            ZRoutedRpc.instance.Register<string, string>("RP_RepData", RpcRepData);
            ZRoutedRpc.instance.Register<string>("RP_Switch", RpcSwitch);
            ZRoutedRpc.instance.Register<string, string>("RP_PvpKill", RpcPvpKill);
            ZRoutedRpc.instance.Register<int>("RP_PvpCoins", RpcPvpCoins);
            ZRoutedRpc.instance.Register<string>("RP_CoMsg", RpcCoMsg);
            ZRoutedRpc.instance.Register<string>("RP_RosterReq", RpcRosterReq);
            ZRoutedRpc.instance.Register<string>("RP_RosterData", RpcRosterData);
            ZRoutedRpc.instance.Register<string, string, string>("RP_CoCmd", RpcCoCmd);
        }

        private static void RpcNotice(long sender, string career, string text)
        {
            if (Player.m_localPlayer != null && LocalCareer() == career)
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, text);
        }

        // Server: a player told us which career he chose.
        private static void RpcCareer(long sender, string playerName, string career)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            ManagerNPCRP.NpcDatabase db = Db;
            if (db == null) return;
            career = ResolveCareerKey(db, career);
            if (career == null) return;

            string resolvedPlayer = CareerManager.ResolveSenderName(sender, playerName);
            if (resolvedPlayer == null)
            {
                ManagerNPCRP.Instance?.Log.LogWarning(
                    "RPCareer rejected: sender " + sender + " does not match player '" + playerName + "'.");
                return;
            }

            RepWorld ws = Ws();
            string current = ws.playerCareer.TryGetValue(resolvedPlayer, out string existing) ? existing : null;

            bool isAdmin = CareerManager.IsSenderAdmin(sender);

            if (!string.IsNullOrEmpty(current) && !isAdmin) return;

            ws.playerCareer[resolvedPlayer] = career;
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
                    if (p != null) { killer = p.GetPlayerName(); QuestEngine.RelayNpcKill(p, id, zdo.GetString(ClanKey, "")); }
                }

                ZRoutedRpc.instance.InvokeRoutedRPC(RpcAccess.ServerPeerId(), "RP_Death",
                    id, zdo.GetString(ClanKey, ""), killer);
            }
            catch (Exception ex)
            {
                ManagerNPCRP.Instance?.Log.LogWarning("OnDeath hook failed: " + ex.Message);
            }
        }

        // ============================================================ PvP + COMPANIES (roster)

        private const string CompanyKey = "RP_Company";
        private static readonly Dictionary<string, float> PvpLast = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, KeyValuePair<string, float>> Invites =
            new Dictionary<string, KeyValuePair<string, float>>(StringComparer.OrdinalIgnoreCase);   // invitee -> (company id, expiry)
        internal static RosterSnap Roster;
        private static float _lastRosterReq = -999f;
        private static MethodInfo _isPvp;
        private static GUIStyle _lblStyle;

        // ---- who is who (player ZDOs carry career and company, written by their owner)
        internal static string CareerOf(Player p)
        {
            if (p == null) return "";
            if (p == Player.m_localPlayer) return HasChosenCareer() ? LocalCareer() : "";
            ZNetView nv = p.GetComponent<ZNetView>();
            ZDO z = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            return z != null ? z.GetString(CareerKey, "") : "";
        }
        private static string CompanyNameOf(Player p)
        {
            if (p == null) return "";
            ZNetView nv = p.GetComponent<ZNetView>();
            ZDO z = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            return z != null ? z.GetString(CompanyKey, "") : "";
        }
        internal static bool PvpOn(Player p)
        {
            if (p == null) return false;
            if (_isPvp == null) _isPvp = typeof(Player).GetMethod("IsPVPEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_isPvp == null || _isPvp.GetParameters().Length != 0) return false;
            try { return (bool)_isPvp.Invoke(p, null); } catch { return false; }
        }
        private static string FactionName(string f)
        {
            switch (f)
            {
                case "DouzeCouronnes": return "Douze Couronnes";
                case "CamaradesAzures": return "Camarades Azurés";
                case "HordeSolaire": return "Horde Solaire";
                default: return f ?? "";
            }
        }

        // ---- friendly fire: two players of the same allegiance never hurt each other (unless FriendlyFire = true)
        internal static bool DamagePrefix(Character __instance, object[] __args)
        {
            try
            {
                if (Pvp.FriendlyFire == null || Pvp.FriendlyFire.Value || __args == null) return true;
                Player victim = __instance as Player;
                if (victim == null) return true;
                HitData hit = null;
                foreach (object o in __args) { hit = o as HitData; if (hit != null) break; }
                Player attacker = hit != null ? hit.GetAttacker() as Player : null;
                if (attacker == null || attacker == victim) return true;
                string a = CareerOf(attacker), b = CareerOf(victim);
                return a.Length == 0 || !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        // ---- the victim's own client reports who killed it; the server validates and applies
        internal static void PlayerDeathPostfix(Character __instance)
        {
            try
            {
                Player victim = __instance as Player;
                if (victim == null || victim != Player.m_localPlayer || ZRoutedRpc.instance == null) return;
                HitData hit = LastHit != null ? LastHit.GetValue(__instance) as HitData : null;
                Player killer = hit != null ? hit.GetAttacker() as Player : null;
                if (killer == null || killer == victim) return;
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcAccess.ServerPeerId(), "RP_PvpKill", killer.GetPlayerName(), victim.GetPlayerName());
            }
            catch (Exception ex) { ManagerNPCRP.Instance?.Log.LogWarning("PvP death hook failed: " + ex.Message); }
        }

        private static string SenderName(long sender)
        {
            if (ZNet.instance == null) return null;
            if (sender == ZNet.GetUID()) return Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : null;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers()) if (peer.m_uid == sender) return peer.m_playerName;
            return null;
        }
        private static bool OnlineExact(string name, out long uid, out Vector3 pos)
        {
            uid = 0L; pos = Vector3.zero;
            if (string.IsNullOrEmpty(name) || ZNet.instance == null) return false;
            if (Player.m_localPlayer != null && string.Equals(Player.m_localPlayer.GetPlayerName(), name, StringComparison.OrdinalIgnoreCase))
            { uid = ZNet.GetUID(); pos = Player.m_localPlayer.transform.position; return true; }
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                if (string.Equals(peer.m_playerName, name, StringComparison.OrdinalIgnoreCase)) { uid = peer.m_uid; pos = peer.m_refPos; return true; }
            return false;
        }
        private static string CareerOfName(RepWorld ws, string name)
        {
            foreach (KeyValuePair<string, string> kv in ws.playerCareer)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return "";
        }
        private static void Tell(long to, string text)
        {
            if (to == ZNet.GetUID() && Player.m_localPlayer != null) { Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, text); return; }
            if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(to, "RP_CoMsg", text);
        }
        private static void RpcCoMsg(long sender, string text)
        {
            if (Player.m_localPlayer != null) Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, text);
        }
        private static void RpcPvpCoins(long sender, int n)
        {
            QuestEngine.GrantItem("Coins", n);
        }

        private static void RpcPvpKill(long sender, string killer, string victim)
        {
            try
            {
                ManagerNPCRP plugin = ManagerNPCRP.Instance;
                if (plugin == null || plugin.Database == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (Pvp.Enabled == null || !Pvp.Enabled.Value) return;
                string v = CareerManager.ResolveSenderName(sender, victim);          // only the victim's own client may report its death
                if (v == null || string.Equals(killer, v, StringComparison.OrdinalIgnoreCase)) return;
                long kuid, vuid; Vector3 kpos, vpos;
                if (!OnlineExact(killer, out kuid, out kpos) || !OnlineExact(v, out vuid, out vpos)) return;
                if (Vector3.Distance(kpos, vpos) > 200f) return;                      // the killer must have been near

                RepWorld ws = Ws();
                string kc = CareerOfName(ws, killer), vc = CareerOfName(ws, v);
                CareerDefinition kd, vd;
                if (kc.Length == 0 || vc.Length == 0 || !plugin.Database.careers.TryGetValue(kc, out kd) || !plugin.Database.careers.TryGetValue(vc, out vd)) return;
                if (kd == null || vd == null || string.Equals(kd.faction, vd.faction, StringComparison.OrdinalIgnoreCase)) return;   // same allegiance: nothing

                string key = killer + ">" + v; float last;
                if (PvpLast.TryGetValue(key, out last) && Time.time - last < Pvp.CooldownSeconds.Value) return;                  // no farming
                PvpLast[key] = Time.time;

                int kn, dn;
                ws.pvpKills.TryGetValue(killer, out kn); ws.pvpKills[killer] = kn + 1;
                ws.pvpDeaths.TryGetValue(v, out dn); ws.pvpDeaths[v] = dn + 1;
                AddFaction(plugin, kc, kd.faction, Pvp.KillPoints.Value);
                AddFaction(plugin, vc, vd.faction, -Pvp.DeathPoints.Value);
                SyncAll(plugin, kc, true);
                SyncAll(plugin, vc, true);
                Save();

                int coins = Pvp.KillCoins.Value;
                if (coins > 0)
                {
                    if (kuid == ZNet.GetUID()) QuestEngine.GrantItem("Coins", coins);
                    else if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(kuid, "RP_PvpCoins", coins);
                }
                Notice(kc, killer + " a terrassé " + v + " (" + FactionName(vd.faction) + ") : +" + Pvp.KillPoints.Value + " de réputation.");
                Notice(vc, v + " est tombé sous les coups de " + killer + " (" + FactionName(kd.faction) + ") : -" + Pvp.DeathPoints.Value + " de réputation.");
            }
            catch (Exception ex) { ManagerNPCRP.Instance?.Log.LogWarning("PvP kill failed: " + ex.Message); }
        }

        // ---- companies: up to Companies.MaxMembers players of the SAME allegiance, known to everybody
        private static Company CompanyOfPlayer(RepWorld ws, string name)
        {
            foreach (Company c in ws.companies.Values)
                foreach (string m in c.members) if (string.Equals(m, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }
        private static string Clean(string s, int max)
        {
            s = (s ?? "").Trim();
            foreach (char ch in new[] { '|', ';', '~', ',', '\n', '\r', '<', '>' }) s = s.Replace(ch.ToString(), "");
            return s.Length > max ? s.Substring(0, max) : s;
        }
        private static void PushRoster(string name)
        {
            long uid; Vector3 pos;
            if (!OnlineExact(name, out uid, out pos)) return;
            string payload = BuildRoster(Ws(), name);
            if (uid == ZNet.GetUID()) ApplyRoster(payload);
            else if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(uid, "RP_RosterData", payload);
        }
        private static string BuildRoster(RepWorld ws, string me)
        {
            string career = CareerOfName(ws, me);
            Company mine = CompanyOfPlayer(ws, me);
            string invite = "";
            KeyValuePair<string, float> inv;
            if (Invites.TryGetValue(me, out inv) && Time.time < inv.Value)
            {
                Company ic; if (ws.companies.TryGetValue(inv.Key, out ic)) invite = Clean(ic.name, 24) + "~" + Clean(ic.leader, 24);
            }
            List<string> members = new List<string>();
            foreach (KeyValuePair<string, string> kv in ws.playerCareer)
            {
                if (!string.Equals(kv.Value, career, StringComparison.OrdinalIgnoreCase)) continue;
                long u; Vector3 p;
                Company c = CompanyOfPlayer(ws, kv.Key);
                int pk, pd;
                ws.pvpKills.TryGetValue(kv.Key, out pk); ws.pvpDeaths.TryGetValue(kv.Key, out pd);
                members.Add(Clean(kv.Key, 32) + "~" + (OnlineExact(kv.Key, out u, out p) ? "1" : "0") + "~" + (c != null ? Clean(c.name, 24) : "") + "~" + pk + "~" + pd);
            }
            List<string> comps = new List<string>();
            foreach (Company c in ws.companies.Values)
                comps.Add(c.id + "~" + Clean(c.name, 24) + "~" + c.career + "~" + Clean(c.leader, 32) + "~" +
                          string.Join(",", c.members.Select(m => Clean(m, 32)).ToArray()));
            return career + "|" + Pvp.CompanyMax.Value + "|" + (mine != null ? mine.id : "") + "|" + invite + "|" +
                   string.Join(";", members.ToArray()) + "|" + string.Join(";", comps.ToArray());
        }
        private static void ApplyRoster(string payload)
        {
            string[] s = (payload ?? "").Split('|');
            if (s.Length < 6) return;
            RosterSnap r = new RosterSnap();
            r.career = s[0]; int mx; r.max = int.TryParse(s[1], out mx) ? mx : 3; r.companyId = s[2];
            string[] iv = s[3].Split('~');
            if (iv.Length >= 2) { r.inviteCompany = iv[0]; r.inviter = iv[1]; }
            foreach (string rec in s[4].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = rec.Split('~');
                if (f.Length >= 3)
                {
                    RosterMember rm = new RosterMember { name = f[0], online = f[1] == "1", company = f[2] };
                    if (f.Length >= 5) { int k, dd; int.TryParse(f[3], out k); int.TryParse(f[4], out dd); rm.kills = k; rm.deaths = dd; }
                    r.members.Add(rm);
                }
            }
            foreach (string rec in s[5].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = rec.Split('~');
                if (f.Length >= 5)
                {
                    CompanyInfo ci = new CompanyInfo { id = f[0], name = f[1], career = f[2], leader = f[3] };
                    ci.members.AddRange(f[4].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                    r.companies.Add(ci);
                    if (ci.id == r.companyId) r.companyName = ci.name;
                }
            }
            r.at = Time.time;
            Roster = r;
            Player lp = Player.m_localPlayer;                                      // publish my company on my own player ZDO (labels / lore)
            if (lp != null)
            {
                ZNetView nv = lp.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid() && nv.IsOwner() && nv.GetZDO() != null && nv.GetZDO().GetString(CompanyKey, "") != r.companyName)
                    nv.GetZDO().Set(CompanyKey, r.companyName);
            }
        }
        private static void RpcRosterData(long sender, string payload) { ApplyRoster(payload); }
        private static void RpcRosterReq(long sender, string clientCareer)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string name = SenderName(sender);
            if (name == null) return;
            string cur = CareerOfName(Ws(), name);                   // a player who inherited an allegiance while offline receives it now
            if (cur.Length > 0 && string.IsNullOrEmpty(clientCareer) && ZRoutedRpc.instance != null)
            {
                if (sender == ZNet.GetUID()) { if (Player.m_localPlayer != null) RpcSetCareer(sender, cur); }
                else ZRoutedRpc.instance.InvokeRoutedRPC(sender, "RP_SetCareer", cur);
            }
            PushRoster(name);
        }
        internal static void RequestRoster(bool force)
        {
            if (!force && Time.time - _lastRosterReq < 20f) return;
            _lastRosterReq = Time.time;
            if (ZNet.instance != null && ZNet.instance.IsServer() && Player.m_localPlayer != null) { PushRoster(Player.m_localPlayer.GetPlayerName()); return; }
            if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(RpcAccess.ServerPeerId(), "RP_RosterReq", HasChosenCareer() ? LocalCareer() : "");
        }
        internal static void CompanyCmd(string cmd, string a1, string a2)
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcAccess.ServerPeerId(), "RP_CoCmd", cmd ?? "", a1 ?? "", a2 ?? "");
            _lastRosterReq = -999f;
        }

                private static void SetMemberCareer(RepWorld ws, string player, string career)
        {
            ws.playerCareer[player] = career;
            long uid; Vector3 pos;
            if (!OnlineExact(player, out uid, out pos)) return;               // offline: it will be pushed at the next login (roster request)
            if (uid == ZNet.GetUID()) { if (Player.m_localPlayer != null) RpcSetCareer(uid, career); }
            else if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(uid, "RP_SetCareer", career);
        }
        private static void RemoveFromCompany(RepWorld ws, Company c, string player, List<string> touched)
        {
            bool wasLeader = string.Equals(c.leader, player, StringComparison.OrdinalIgnoreCase);
            c.members.RemoveAll(m => string.Equals(m, player, StringComparison.OrdinalIgnoreCase));
            touched.Add(player); touched.AddRange(c.members);
            if (c.members.Count == 0) ws.companies.Remove(c.id);
            else if (wasLeader) c.leader = c.members[0];
        }
        /// <summary>Members without an allegiance inherit the one of the company's faction.</summary>
        private static void PropagateCareer(RepWorld ws, Company c, List<string> touched)
        {
            if (c.career.Length == 0) return;
            foreach (string m in c.members)
                if (CareerOfName(ws, m).Length == 0) { SetMemberCareer(ws, m, c.career); touched.Add(m); }
        }

        internal static void SwitchAllegiance(string career)
        {
            if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(RpcAccess.ServerPeerId(), "RP_Switch", career ?? "");
            _lastRosterReq = -999f;
        }
        private static void RpcSwitch(long sender, string input)
        {
            try
            {
                ManagerNPCRP plugin = ManagerNPCRP.Instance;
                if (plugin == null || plugin.Database == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                string me = SenderName(sender);
                if (me == null) return;
                string key = ResolveCareerKey(plugin.Database, input);
                if (key == null) { Tell(sender, "Allégeance inconnue."); return; }
                RepWorld ws = Ws();
                string cur = CareerOfName(ws, me);
                if (string.Equals(cur, key, StringComparison.OrdinalIgnoreCase)) { Tell(sender, "Vous avez déjà cette allégeance."); return; }
                if (cur.Length > 0)
                {
                    if (Pvp.AllowSwitch != null && !Pvp.AllowSwitch.Value) { Tell(sender, "Le changement d'allégeance est désactivé sur ce serveur."); return; }
                    long last; double hours = Pvp.SwitchCooldownHours != null ? Pvp.SwitchCooldownHours.Value : 0.0;
                    if (hours > 0.0 && ws.switchAt.TryGetValue(me, out last))
                    {
                        double left = hours - (DateTime.UtcNow.Ticks - last) / (double)TimeSpan.TicksPerHour;
                        if (left > 0.0) { Tell(sender, "Prochain changement d'allégeance possible dans " + Math.Ceiling(left) + " h."); return; }
                    }
                }
                List<string> touched = new List<string> { me };
                Company c = CompanyOfPlayer(ws, me);
                if (c != null && c.career.Length > 0 && !string.Equals(c.career, key, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveFromCompany(ws, c, me, touched);
                    Tell(sender, "Vous quittez la compagnie « " + c.name + " » : son allégeance est différente.");
                }
                SetMemberCareer(ws, me, key);
                if (cur.Length > 0) ws.switchAt[me] = DateTime.UtcNow.Ticks;
                Save();
                Tell(sender, cur.Length == 0 ? "Allégeance choisie." : "Allégeance changée : la réputation de votre ancienne faction ne vous suit pas.");
                foreach (string n in touched.Distinct(StringComparer.OrdinalIgnoreCase)) PushRoster(n);
            }
            catch (Exception ex) { ManagerNPCRP.Instance?.Log.LogWarning("Allegiance switch failed: " + ex.Message); }
        }

        private static void RpcCoCmd(long sender, string cmd, string a1, string a2)
        {
            try
            {
                ManagerNPCRP plugin = ManagerNPCRP.Instance;
                if (plugin == null || plugin.Database == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                string me = SenderName(sender);
                if (me == null) return;
                RepWorld ws = Ws();
                string career = CareerOfName(ws, me);
                Company mine = CompanyOfPlayer(ws, me);
                bool leader = mine != null && string.Equals(mine.leader, me, StringComparison.OrdinalIgnoreCase);
                List<string> touched = new List<string> { me };
                string arg = (a1 ?? "").Trim();

                switch ((cmd ?? "").ToLowerInvariant())
                {
                    case "create":
                        {
                            string name = Clean(arg, 24);
                            if (mine != null) { Tell(sender, "Vous êtes déjà dans la compagnie « " + mine.name + " »."); break; }
                            if (name.Length < 3) { Tell(sender, "Nom de compagnie trop court (3 lettres minimum)."); break; }
                            if (ws.companies.Values.Any(x => string.Equals(x.name, name, StringComparison.OrdinalIgnoreCase))) { Tell(sender, "Ce nom de compagnie existe déjà."); break; }
                            Company c = new Company { id = "CMP-" + (++ws.companyCounter), name = name, career = career, leader = me };
                            c.members.Add(me); ws.companies[c.id] = c; Save();
                            CareerDefinition cd0 = null;
                            if (career.Length > 0) plugin.Database.careers.TryGetValue(career, out cd0);
                            string fl = cd0 != null ? FactionName(cd0.faction) : "sans allégeance";
                            if (ZRoutedRpc.instance != null)                       // lore: every player learns of the new company
                                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "RP_CoMsg",
                                    "Une nouvelle compagnie est née : « " + name + " » (" + fl + "), sous la bannière de " + me + ".");
                            break;
                        }
                    case "invite":
                        {
                            if (!leader) { Tell(sender, "Seul le chef de compagnie peut ajouter des joueurs."); break; }
                            if (mine.members.Count >= Pvp.CompanyMax.Value) { Tell(sender, "La compagnie est complète (" + Pvp.CompanyMax.Value + ")."); break; }
                            long tuid; Vector3 tp;
                            if (!OnlineExact(arg, out tuid, out tp)) { Tell(sender, "Joueur introuvable ou hors ligne."); break; }
                            string tc = CareerOfName(ws, arg);
                            if (tc.Length > 0 && mine.career.Length > 0 && !string.Equals(tc, mine.career, StringComparison.OrdinalIgnoreCase)) { Tell(sender, "Ce joueur a une autre allégeance que la compagnie."); break; }
                            if (CompanyOfPlayer(ws, arg) != null) { Tell(sender, "Ce joueur est déjà dans une compagnie."); break; }
                            Invites[arg] = new KeyValuePair<string, float>(mine.id, Time.time + 180f);
                            Tell(tuid, me + " vous invite dans la compagnie « " + mine.name + " ». Onglet Faction (ou rp_company accept).");
                            Tell(sender, "Invitation envoyée à " + arg + ".");
                            touched.Add(arg);
                            break;
                        }
                    case "accept":
                        {
                            KeyValuePair<string, float> inv; Company ic;
                            if (mine != null) { Tell(sender, "Vous êtes déjà dans une compagnie."); break; }
                            if (!Invites.TryGetValue(me, out inv) || Time.time > inv.Value || !ws.companies.TryGetValue(inv.Key, out ic)) { Tell(sender, "Aucune invitation valide."); break; }
                            if (ic.members.Count >= Pvp.CompanyMax.Value) { Tell(sender, "La compagnie est complète."); break; }
                            if (career.Length > 0 && ic.career.Length > 0 && !string.Equals(career, ic.career, StringComparison.OrdinalIgnoreCase)) { Tell(sender, "Votre allégeance diffère de celle de la compagnie."); break; }
                            ic.members.Add(me); Invites.Remove(me);
                            if (ic.career.Length == 0 && career.Length > 0) ic.career = career;     // the company takes the allegiance of its first member who has one
                            PropagateCareer(ws, ic, touched);                                          // the others without allegiance inherit it
                            Save();
                            foreach (string m in ic.members)
                            {
                                long u; Vector3 p;
                                if (OnlineExact(m, out u, out p) && !string.Equals(m, me, StringComparison.OrdinalIgnoreCase)) Tell(u, me + " rejoint la compagnie « " + ic.name + " ».");
                                touched.Add(m);
                            }
                            Tell(sender, "Vous rejoignez la compagnie « " + ic.name + " ».");
                            break;
                        }
                    case "leave":
                        {
                            if (mine == null) { Tell(sender, "Vous n'êtes dans aucune compagnie."); break; }
                            RemoveFromCompany(ws, mine, me, touched);
                            Save(); Tell(sender, "Vous quittez la compagnie « " + mine.name + " ».");
                            break;
                        }
                    case "kick":
                        {
                            if (!leader) { Tell(sender, "Seul le chef de compagnie peut renvoyer un membre."); break; }
                            string who = mine.members.FirstOrDefault(m => string.Equals(m, arg, StringComparison.OrdinalIgnoreCase));
                            if (who == null || string.Equals(who, me, StringComparison.OrdinalIgnoreCase)) { Tell(sender, "Membre introuvable."); break; }
                            RemoveFromCompany(ws, mine, who, touched); Save();
                            long ku; Vector3 kp; if (OnlineExact(who, out ku, out kp)) Tell(ku, "Vous avez été renvoyé de la compagnie « " + mine.name + " ».");
                            break;
                        }
                    case "transfer":
                        {
                            if (!leader) { Tell(sender, "Seul le chef de compagnie peut transmettre son rang."); break; }
                            string heir = mine.members.FirstOrDefault(m => string.Equals(m, arg, StringComparison.OrdinalIgnoreCase));
                            if (heir == null || string.Equals(heir, me, StringComparison.OrdinalIgnoreCase)) { Tell(sender, "Choisissez un autre membre de la compagnie."); break; }
                            mine.leader = heir; Save(); touched.AddRange(mine.members);
                            long hu; Vector3 hp;
                            if (OnlineExact(heir, out hu, out hp)) Tell(hu, "Vous devenez chef de la compagnie « " + mine.name + " ».");
                            Tell(sender, heir + " est maintenant chef de la compagnie.");
                            break;
                        }
                    case "setcareer":
                        {
                            if (!leader) { Tell(sender, "Seul le chef de compagnie peut affecter la compagnie à une faction."); break; }
                            string key = ResolveCareerKey(plugin.Database, arg);
                            if (key == null) { Tell(sender, "Allégeance inconnue."); break; }
                            if (career.Length > 0 && !string.Equals(career, key, StringComparison.OrdinalIgnoreCase)) { Tell(sender, "Votre propre allégeance est différente : changez-la d'abord."); break; }
                            string clash = mine.members.FirstOrDefault(m => { string mc = CareerOfName(ws, m); return mc.Length > 0 && !string.Equals(mc, key, StringComparison.OrdinalIgnoreCase); });
                            if (clash != null) { Tell(sender, clash + " a une autre allégeance."); break; }
                            mine.career = key;
                            PropagateCareer(ws, mine, touched);
                            Save(); touched.AddRange(mine.members);
                            foreach (string m in mine.members)
                            {
                                long u2; Vector3 p2;
                                if (OnlineExact(m, out u2, out p2)) Tell(u2, "La compagnie « " + mine.name + " » est affectée à l'allégeance : " + key + ".");
                            }
                            break;
                        }
                    case "disband":
                        {
                            if (!leader) { Tell(sender, "Seul le chef de compagnie peut la dissoudre."); break; }
                            touched.AddRange(mine.members); ws.companies.Remove(mine.id); Save();
                            if (ZRoutedRpc.instance != null)
                                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "RP_CoMsg", "La compagnie « " + mine.name + " » est dissoute.");
                            break;
                        }
                    default:
                        Tell(sender, "rp_company create <nom> | invite <joueur> | accept | leave | kick <joueur> | transfer <joueur> | setcareer <allégeance> | disband | list");
                        break;
                }
                foreach (string n in touched.Distinct(StringComparer.OrdinalIgnoreCase)) PushRoster(n);
            }
            catch (Exception ex) { ManagerNPCRP.Instance?.Log.LogWarning("Company command failed: " + ex.Message); }
        }

        internal static void CompanyConsole(string[] args)
        {
            Player lp = Player.m_localPlayer;
            if (lp == null) return;
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            if (sub == "list")
            {
                RequestRoster(true);
                RosterSnap r = Roster;
                if (r == null) { Out("Aucune donnée de compagnie pour l'instant : réessayez dans un instant."); return; }
                foreach (CompanyInfo c in r.companies) Out("  " + c.name + " [" + c.career + "] chef " + c.leader + " : " + string.Join(", ", c.members.ToArray()));
                if (r.companies.Count == 0) Out("  aucune compagnie");
                return;
            }
            string rest = args.Length > 2 ? string.Join(" ", args.Skip(2).ToArray()) : "";
            CompanyCmd(sub, rest, "");
        }

        // ---- label above a player that has PvP switched on: his faction (and company)
        internal static void DrawPvpLabels()
        {
            if (Pvp.ShowLabels == null || !Pvp.ShowLabels.Value) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            Camera cam = Camera.main; Player lp = Player.m_localPlayer;
            if (cam == null || lp == null || Db == null) return;
            if (_lblStyle == null)
            {
                _lblStyle = new GUIStyle(GUI.skin.label);
                _lblStyle.alignment = TextAnchor.UpperCenter; _lblStyle.richText = true; _lblStyle.wordWrap = false; _lblStyle.fontSize = 14;
            }
            string mine = HasChosenCareer() ? LocalCareer() : "";
            CareerDefinition mycd = null; if (mine.Length > 0) Db.careers.TryGetValue(mine, out mycd);
            foreach (Player p in Player.GetAllPlayers())
            {
                if (p == null || p == lp || !PvpOn(p)) continue;
                Vector3 wp = p.transform.position + Vector3.up * 2.6f;
                if (Vector3.Distance(cam.transform.position, wp) > 70f) continue;
                Vector3 sp = cam.WorldToScreenPoint(wp);
                if (sp.z <= 0f) continue;
                string c = CareerOf(p); CareerDefinition cd = null;
                if (c.Length > 0) Db.careers.TryGetValue(c, out cd);
                string fac = cd != null ? FactionName(cd.faction) : "Sans allégeance";
                string comp = CompanyNameOf(p);
                Color col = new Color(0.75f, 0.75f, 0.75f);
                if (cd != null && mycd != null)
                {
                    if (string.Equals(cd.faction, mycd.faction, StringComparison.OrdinalIgnoreCase)) col = new Color(0.45f, 0.95f, 0.45f);
                    else if (mycd.enemyFactions != null && mycd.enemyFactions.Contains(cd.faction)) col = new Color(1f, 0.4f, 0.35f);
                    else col = new Color(1f, 0.75f, 0.3f);
                }
                string text = "<b>[PvP] " + fac + "</b>" + (comp.Length > 0 ? "\n" + comp : "");
                Rect rc = new Rect(sp.x - 120f, Screen.height - sp.y - 26f, 240f, 48f);
                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.85f); GUI.Label(new Rect(rc.x + 1f, rc.y + 1f, rc.width, rc.height), text, _lblStyle);
                GUI.color = col; GUI.Label(rc, text, _lblStyle);
                GUI.color = old;
            }
        }

        // ============================================================ client: career choice

        internal static void ClientTick()
        {
            if (Time.time < _nextClientTick) return;
            _nextClientTick = Time.time + 5f;

            Player lp = Player.m_localPlayer;
            if (lp == null || ZRoutedRpc.instance == null || Db == null) return;

            ZNetView pview = lp.GetComponent<ZNetView>();
            if (pview != null && pview.IsValid() && pview.IsOwner())
            {
                ZDO pz = pview.GetZDO();
                string mine = HasChosenCareer() ? LocalCareer() : "";
                if (pz != null && pz.GetString(CareerKey, "") != mine) pz.Set(CareerKey, mine);
            }

            if (!HasChosenCareer() && Time.time - _lastRosterReq > 45f) RequestRoster(true);
            if (HasChosenCareer())
            {
                if (!_announced)
                {
                    _announced = true;
                    ZRoutedRpc.instance.InvokeRoutedRPC(RpcAccess.ServerPeerId(), "RP_Career",
                        lp.GetPlayerName(), LocalCareer());
                }
                if (Time.time - _lastRosterReq > 45f) RequestRoster(true);       // keeps my company / faction list fresh (also feeds the PvP labels)
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
            if (arg == "bind") { BindCommandArgs(args, 2); return; }

            if (arg == "list")
            {
                foreach (KeyValuePair<string, CareerDefinition> kv in db.careers)
                    Out("  " + kv.Key + " - " + kv.Value.name + " : " + kv.Value.description);

                lp.Message(MessageHud.MessageType.TopLeft,
                    "Allégeance actuelle : " + (HasChosenCareer() ? LocalCareer() : "aucune"));
                return;
            }

            bool bind = arg == "bind";
            bool force = arg == "force";

            if (bind)
            {
                if (args.Length < 4)
                {
                    lp.Message(MessageHud.MessageType.TopLeft,
                        "Usage : rp_career bind <joueur> <carrière>");
                    return;
                }

                if (!(ZNet.instance != null && ZNet.instance.LocalPlayerIsAdminOrHost()))
                {
                    lp.Message(MessageHud.MessageType.TopLeft, "Réservé aux administrateurs.");
                    return;
                }

                if (!(ZNet.instance != null && ZNet.instance.IsServer()))
                {
                    lp.Message(MessageHud.MessageType.TopLeft,
                        "rp_career bind doit être exécuté sur l'hôte/serveur.");
                    return;
                }

                string targetPlayer = args[2];
                string targetCareer = args[3];

                CareerManager.CareerResult result = CareerManager.Assign(
                    plugin, targetPlayer, targetCareer, adminOverride: true);

                string message;
                switch (result)
                {
                    case CareerManager.CareerResult.Ok:
                        message = "Carrière '" + targetCareer + "' attribuée à " + targetPlayer + ".";
                        break;
                    case CareerManager.CareerResult.UnknownCareer:
                        message = "Carrière inconnue : " + targetCareer;
                        break;
                    case CareerManager.CareerResult.UnknownPlayer:
                        message = "Joueur introuvable ou ambigu : " + targetPlayer;
                        break;
                    case CareerManager.CareerResult.AlreadyChosen:
                        message = targetPlayer + " a déjà une carrière.";
                        break;
                    default:
                        message = "Attribution refusée.";
                        break;
                }

                lp.Message(MessageHud.MessageType.TopLeft, message);
                return;
            }

            string choiceRaw = force ? (args.Length > 2 ? args[2] : "") : arg;
            string choice = ResolveCareerKey(db, choiceRaw) ?? choiceRaw;

            if (!db.careers.ContainsKey(choice))
            {
                Out("Allégeance inconnue : '" + choiceRaw + "'. Disponibles : " + CareerList(db) +
                    " (" + db.careers.Count + " carrière(s) chargée(s))");
                lp.Message(MessageHud.MessageType.TopLeft, "Allégeance inconnue : " + choiceRaw + " (voir console F5)");
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

            if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(
                    RpcAccess.ServerPeerId(),
                    "RP_Career",
                    lp.GetPlayerName(),
                    choice);
            }

            lp.m_customData[CareerKey] = choice;
            _announced = false;
            lp.Message(MessageHud.MessageType.Center, "Allégeance : " + db.careers[choice].name);
        }
        // ============================================================ career resolution + bind

        private static void Out(string text)
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin != null) plugin.Log.LogInfo(text);
            if (Console.instance != null) Console.instance.Print(text);
        }

        private static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string d = s.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char c in d)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) !=
                    System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Maps user input (key, display name, accents/case ignored, unique prefix) to the real career key.</summary>
        internal static string ResolveCareerKey(ManagerNPCRP.NpcDatabase db, string input)
        {
            if (db == null || db.careers == null || string.IsNullOrWhiteSpace(input)) return null;
            string want = Fold(input);
            foreach (KeyValuePair<string, CareerDefinition> kv in db.careers)
                if (Fold(kv.Key) == want) return kv.Key;
            foreach (KeyValuePair<string, CareerDefinition> kv in db.careers)
                if (kv.Value != null && Fold(kv.Value.name) == want) return kv.Key;

            foreach (KeyValuePair<string, CareerDefinition> kv in db.careers)
            {
                if (kv.Value == null || kv.Value.aliases == null) continue;
                foreach (string al in kv.Value.aliases)
                    if (Fold(al) == want) return kv.Key;
            }

            string found = null;
            foreach (KeyValuePair<string, CareerDefinition> kv in db.careers)
            {
                if (!Fold(kv.Key).StartsWith(want, StringComparison.Ordinal)) continue;
                if (found != null) return null;   // ambiguous prefix
                found = kv.Key;
            }
            return found;
        }

        internal static string CareerList(ManagerNPCRP.NpcDatabase db)
        {
            return db == null || db.careers == null || db.careers.Count == 0
                ? "(aucune)" : string.Join(" | ", db.careers.Keys.ToArray());
        }

        /// <summary>rp_bind / rp_career bind: args[first..n-2] = player name (spaces allowed), args[n-1] = career.</summary>
        internal static void BindCommandArgs(string[] args, int first)
        {
            if (args == null || args.Length - first < 2)
            {
                Out("Usage : rp_playercareer <joueur> <carrière>   (carrières : " + CareerList(Db) + ")");
                return;
            }
            string career = args[args.Length - 1];
            string player = string.Join(" ", args.Skip(first).Take(args.Length - first - 1).ToArray());
            BindCommand(player, career);
        }

        internal static void BindCommand(string playerName, string careerInput)
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null || plugin.Database == null) return;
            ManagerNPCRP.NpcDatabase db = plugin.Database;

            string career = ResolveCareerKey(db, careerInput);
            if (career == null)
            {
                Out("Carrière inconnue : '" + careerInput + "'. Disponibles : " + CareerList(db));
                return;
            }

            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                Out(DoBind(plugin, playerName, career));
                return;
            }

            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcAccess.ServerPeerId(), "RP_Bind", playerName, career);
            Out("Demande d'attribution envoyée au serveur...");
        }

        // Server: record the career AND push it to the target client (the client reads it from its own save).
        private static string DoBind(ManagerNPCRP plugin, string playerName, string career)
        {
            string resolved;
            long uid;
            if (!CareerManager.TryResolveOnlinePlayer(playerName, out resolved, out uid))
                return "Joueur introuvable ou ambigu : " + playerName;

            CareerManager.CareerResult res = CareerManager.Assign(plugin, resolved, career, true);
            if (res != CareerManager.CareerResult.Ok)
                return "Attribution refusée (" + res + ").";

            if (ZRoutedRpc.instance != null)
                if (uid == ZNet.GetUID() && Player.m_localPlayer != null)
                    RpcSetCareer(uid, career);
                else
                    ZRoutedRpc.instance.InvokeRoutedRPC(uid, "RP_SetCareer", career);
            return resolved + " est désormais lié à la carrière '" + career + "'.";
        }

        // Server: an admin client asked for a bind.
        private static void RpcBind(long sender, string playerName, string careerInput)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null || plugin.Database == null) return;

            string msg;
            if (!CareerManager.IsSenderAdmin(sender))
                msg = "Réservé aux administrateurs.";
            else
            {
                string key = ResolveCareerKey(plugin.Database, careerInput);
                msg = key == null ? "Carrière inconnue : " + careerInput
                                  : DoBind(plugin, playerName, key);
            }

            plugin.Log.LogInfo("[RP_Bind] from " + sender + ": " + msg);
            if (ZRoutedRpc.instance != null)
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "RP_Msg", msg);
        }

        // Client: feedback from the server.
        private static void RpcMsg(long sender, string text)
        {
            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, text);
            Out(text);
        }

        // Client: the server assigned my career.
        private static void RpcSetCareer(long sender, string career)
        {
            Player lp = Player.m_localPlayer;
            if (lp == null || Db == null) return;

            bool iAmServer = ZNet.instance != null && ZNet.instance.IsServer();
            long serverId = RpcAccess.ServerPeerId();
            if (!iAmServer && serverId != ZRoutedRpc.Everybody && sender != serverId) return;   // only the server may assign

            string key = ResolveCareerKey(Db, career);
            if (key == null) return;

            string oldKey = HasChosenCareer() ? LocalCareer() : "";
            if (oldKey.Length > 0 && !string.Equals(oldKey, key, StringComparison.OrdinalIgnoreCase)) QuestEngine.DropCareerQuests(lp, key);
            lp.m_customData[CareerKey] = key;
            _announced = false;
            lp.Message(MessageHud.MessageType.Center, "Allégeance : " + Db.careers[key].name);
        }

        // ============================================================ rp_listcareer

        private static string CareerLabel(ManagerNPCRP.NpcDatabase db, string career)
        {
            CareerDefinition cd;
            return db != null && db.careers != null && db.careers.TryGetValue(career ?? "", out cd) && cd != null &&
                   !string.IsNullOrEmpty(cd.name) ? career + " (" + cd.name + ")" : career;
        }

        // Server only: online players first, then players known from earlier sessions.
        private static List<string> BuildCareerLines()
        {
            ManagerNPCRP.NpcDatabase db = Db;
            RepWorld ws = Ws();
            List<string> lines = new List<string>();
            lines.Add("===== CARRIÈRES DES JOUEURS =====");

            HashSet<string> online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Vector3> p in Players())
            {
                if (!online.Add(p.Key)) continue;
                string c;
                bool has = ws.playerCareer.TryGetValue(p.Key, out c) && !string.IsNullOrEmpty(c);
                lines.Add("  [en ligne]   " + p.Key + " => " + (has ? CareerLabel(db, c) : "(aucune)"));
            }
            foreach (KeyValuePair<string, string> kv in ws.playerCareer)
                if (!online.Contains(kv.Key))
                    lines.Add("  [hors ligne] " + kv.Key + " => " + CareerLabel(db, kv.Value));

            if (lines.Count == 1) lines.Add("  (aucun joueur)");
            return lines;
        }

        internal static void ListCareerCommand()
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null || plugin.Database == null) return;

            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                foreach (string l in BuildCareerLines()) Out(l);
                if (Player.m_localPlayer != null)
                    Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "Carrières : voir console (F5)");
                return;
            }

            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcAccess.ServerPeerId(), "RP_List", "");
            Out("Demande de liste envoyée au serveur...");
        }

        // Server: an admin client asked for the list.
        private static void RpcList(long sender, string unused)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZRoutedRpc.instance == null) return;
            if (ManagerNPCRP.Instance == null || ManagerNPCRP.Instance.Database == null) return;

            if (!CareerManager.IsSenderAdmin(sender))
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "RP_Msg", "Réservé aux administrateurs.");
                return;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "RP_Lines", string.Join("\n", BuildCareerLines().ToArray()));
        }

        // Client: multi-line answer from the server.
        private static void RpcLines(long sender, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (string l in text.Split('\n')) Out(l);
            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "Carrières : voir console (F5)");
        }

        internal static void PlayerCareerCommand()
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null)
                return;

            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                plugin.Log.LogInfo("rp_playercareer must run on the host/server (careers are stored server-side).");
                if (Player.m_localPlayer != null)
                    Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "rp_playercareer : hôte/serveur uniquement");
                return;
            }

            RepWorld ws = Ws();
            List<string> lines = new List<string>();
            lines.Add("===== PLAYER CAREERS =====");
            if (ws.playerCareer.Count == 0)
                lines.Add("No registered careers.");
            else
                foreach (KeyValuePair<string, string> kv in ws.playerCareer)
                    lines.Add(kv.Key + " => " + kv.Value);

            foreach (string l in lines)
            {
                plugin.Log.LogInfo(l);
                if (Console.instance != null) Console.instance.Print(l);
            }
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
            if (r == null || !r.enabled || npc == null) return false;
            bool isGuard =
                string.Equals(npc.role, "Guard", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(npc.role, "Soldier", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(npc.role, "Watchman", StringComparison.OrdinalIgnoreCase) ||
                (npc.livingNorsemen != null &&
                 string.Equals(npc.livingNorsemen.profession, "Guard", StringComparison.OrdinalIgnoreCase));
            if (!isGuard) return false;

            string rf = RepFactionOf(npc);
            if (string.IsNullOrEmpty(rf) || rf == "Vagabonds" || npc.clan == "Itinerants") return false;
            return CareerEnemy(LocalCareer(), npc, rf) ||
                   DialogueStages.FlagOn("repfhostile_" + rf) ||
                   (!string.IsNullOrEmpty(npc.clan) && DialogueStages.FlagOn("rephostile_" + npc.clan));
        }

        // ============================================================ career-based hostility

        /// <summary>True once Harmony has patched BaseAI.IsEnemy (per-player hostility replaces the global hostile faction variant).</summary>
        internal static bool EnemyPatchActive;

        internal static bool IsGuardNpc(ManagerNPCRP.NpcDefinition npc)
        {
            if (npc == null) return false;
            return string.Equals(npc.role, "Guard", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(npc.role, "Soldier", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(npc.role, "Watchman", StringComparison.OrdinalIgnoreCase) ||
                   (npc.livingNorsemen != null &&
                    string.Equals(npc.livingNorsemen.profession, "Guard", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The faction of this NPC is listed in careers.&lt;career&gt;.enemyFactions (never the career's own faction, never Vagabonds).</summary>
        internal static bool CareerEnemy(string career, ManagerNPCRP.NpcDefinition npc, string rf)
        {
            ReputationSettings r = Cfg;
            if (r == null || !r.enabled || r.careerHostility == null || !r.careerHostility.enabled) return false;
            if (npc == null || Db == null || string.IsNullOrEmpty(rf) || rf == "Vagabonds" || npc.clan == "Itinerants") return false;

            CareerDefinition cd;
            if (string.IsNullOrEmpty(career) || !Db.careers.TryGetValue(career, out cd) || cd == null ||
                cd.enemyFactions == null) return false;
            if (string.Equals(cd.faction, rf, StringComparison.OrdinalIgnoreCase)) return false;

            foreach (string f in cd.enemyFactions)
                if (string.Equals(f, rf, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Would this NPC treat a player of that career as an enemy? (career rivalry OR hostile reputation flags)</summary>
        internal static bool NpcHostileToCareer(string career, ManagerNPCRP.NpcDefinition npc)
        {
            ReputationSettings r = Cfg;
            if (r == null || !r.enabled || npc == null) return false;

            string rf = RepFactionOf(npc);
            if (string.IsNullOrEmpty(rf) || rf == "Vagabonds" || npc.clan == "Itinerants") return false;
            if ((r.careerHostility == null || r.careerHostility.guardsOnly) && !IsGuardNpc(npc)) return false;

            if (CareerEnemy(career, npc, rf)) return true;
            return DialogueStages.RawOn("repfhostile_" + career + "_" + rf) ||
                   (!string.IsNullOrEmpty(npc.clan) && DialogueStages.RawOn("rephostile_" + career + "_" + npc.clan));
        }

        /// <summary>Career of any player: local one from its save, remote ones from their player ZDO (published by ClientTick).</summary>
        internal static string CareerOfPlayer(Player p)
        {
            if (p == null) return DefaultCareer;
            if (p == Player.m_localPlayer) return LocalCareer();
            ZNetView nv = p.GetComponent<ZNetView>();
            string c = nv != null && nv.GetZDO() != null ? nv.GetZDO().GetString(CareerKey, "") : "";
            return string.IsNullOrEmpty(c) ? DefaultCareer : c;
        }

        // IsEnemy is called very often by the AI: resolve each Viking's NpcDefinition once.
        private static readonly Dictionary<int, ManagerNPCRP.NpcDefinition> NpcCache =
            new Dictionary<int, ManagerNPCRP.NpcDefinition>();

        internal static void ClearNpcCache() { NpcCache.Clear(); }

        private static ManagerNPCRP.NpcDefinition NpcOfViking(Viking v)
        {
            int key = v.GetInstanceID();
            ManagerNPCRP.NpcDefinition npc;
            if (NpcCache.TryGetValue(key, out npc)) return npc;

            if (NpcCache.Count > 3000) NpcCache.Clear();   // destroyed NPCs leave stale ids: start over

            npc = null;
            ZNetView nv = v.GetComponent<ZNetView>();
            ZDO zdo = nv != null ? nv.GetZDO() : null;
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (zdo != null && plugin != null)
            {
                string id = zdo.GetString(RpIdKey, "");
                if (id.Length > 0 && !plugin.Npcs.TryGetValue(id, out npc))
                    PopulationManager.TryResolveGenerated(zdo, id, out npc);
            }
            if (zdo != null) NpcCache[key] = npc;   // not cached while the ZDO is not ready yet
            return npc;
        }

        // Harmony postfix on BaseAI.IsEnemy(Character, Character): decided PER PLAYER, on whichever client owns the AI.
        internal static void IsEnemyPostfix(Character __0, Character __1, ref bool __result)
        {
            if (__result) return;
            try
            {
                Player p = __0 as Player;
                Viking v = __1 as Viking;
                if (p == null || v == null) { p = __1 as Player; v = __0 as Viking; }
                if (p == null || v == null) return;

                ManagerNPCRP.NpcDefinition npc = NpcOfViking(v);
                if (npc == null) return;

                if (NpcHostileToCareer(CareerOfPlayer(p), npc)) __result = true;
            }
            catch { }
        }

        internal static void EnemiesCommand()
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null || plugin.Database == null) return;

            string career = LocalCareer();
            CareerDefinition cd;
            plugin.Database.careers.TryGetValue(career, out cd);

            Out("Carrière : " + career + (HasChosenCareer() ? "" : " (par défaut : aucune choisie)"));
            Out("Factions ennemies : " + (cd != null && cd.enemyFactions != null && cd.enemyFactions.Count > 0
                ? string.Join(", ", cd.enemyFactions.ToArray()) : "(aucune)"));
            Out("Patch BaseAI.IsEnemy : " + (EnemyPatchActive ? "actif" : "INACTIF (méthode introuvable)"));

            int guards = 0, hostile = 0;
            Dictionary<string, int> byFaction = new Dictionary<string, int>();
            foreach (Character c in Character.GetAllCharacters())
            {
                Viking v = c as Viking;
                if (v == null || v.IsDead()) continue;
                string id = IdOf(v);
                if (id.Length == 0) continue;

                ZNetView nv = v.GetComponent<ZNetView>();
                ManagerNPCRP.NpcDefinition npc = null;
                if (!plugin.Npcs.TryGetValue(id, out npc) &&
                    !PopulationManager.TryResolveGenerated(nv != null ? nv.GetZDO() : null, id, out npc)) continue;
                if (npc == null) continue;

                if (IsGuardNpc(npc)) guards++;
                if (!NpcHostileToCareer(career, npc)) continue;
                hostile++;
                string rf = RepFactionOf(npc) ?? "?";
                int n; byFaction.TryGetValue(rf, out n); byFaction[rf] = n + 1;
            }
            Out("PNJ chargés : " + guards + " gardes, " + hostile + " hostile(s)" +
                (byFaction.Count > 0 ? " (" + string.Join(", ", byFaction.Select(kv => kv.Key + " " + kv.Value).ToArray()) + ")" : "") + ".");
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
        internal static bool UiBlocked()
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
            if (best != null)
            {
                if (plugin.Database.settings.debug)
                {
                    plugin.Log.LogInfo("[RP TALK] " + bestId + " distance=" + bestD.ToString("0.0") +
                                       " clan=" + bestNpc.clan + " refusing=" +
                                       ReputationManager.IsRefusing(bestId, bestNpc.clan));
                }
                Talk(lp, best, bestId, bestNpc);
                QuestEngine.AfterTalk(bestId);
            }
            else if (plugin.Database.settings.debug)
            {
                plugin.Log.LogInfo("[RP TALK] no RP NPC within 3.5 m.");
            }
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
            QuestEngine.AppendJournal(lines);
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
            bool virt;
            if (TryVirtual(flag, out virt)) return virt;
            if (ReputationManager.IsScoped(flag)) flag = ReputationManager.Scope(flag);
            return RawOn(flag);
        }
        /// <summary>
        /// Virtual flags, resolved for the LOCAL player (usable in dialogue stages and quest requirements):
        /// career_&lt;id&gt; (chosen career), month_&lt;n&gt; (in-game month &gt;= n),
        /// qactive_&lt;questId&gt; (accepted, ready included), qready_&lt;questId&gt; (objectives done, not handed in), qdone_&lt;questId&gt; (completed).
        /// </summary>
        private static bool TryVirtual(string flag, out bool on)
        {
            on = false;
            if (string.IsNullOrEmpty(flag)) return false;
            if (flag.StartsWith("career_", StringComparison.Ordinal))
            {
                on = ReputationManager.HasChosenCareer() &&
                     string.Equals(ReputationManager.LocalCareer(), flag.Substring(7), StringComparison.OrdinalIgnoreCase);
                return true;
            }
            if (flag.StartsWith("month_", StringComparison.Ordinal))
            {
                int m;
                on = int.TryParse(flag.Substring(6), out m) && QuestEngine.CurrentMonth() >= m;
                return true;
            }
            bool qa = flag.StartsWith("qactive_", StringComparison.Ordinal);
            bool qr = flag.StartsWith("qready_", StringComparison.Ordinal);
            bool qd = flag.StartsWith("qdone_", StringComparison.Ordinal);
            if (qa || qr || qd)
            {
                Player lp = Player.m_localPlayer;
                QDef q;
                if (lp == null || !QuestEngine.Quests.TryGetValue(flag.Substring(qa ? 8 : qr ? 7 : 6), out q)) return true;
                string why;
                QuestState st = QuestEngine.Evaluate(lp, q, out why);
                on = qd ? st == QuestState.Completed : qr ? st == QuestState.Ready : (st == QuestState.Active || st == QuestState.Ready);
                return true;
            }
            return false;
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
            if (QuestEngine.IsEscorted(zdo)) return;           // escorted by a player: no wandering
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
    // =====================================================================
    // QUEST ENGINE  (definitions live in npc_database.json -> "questLines")
    //   * per-player state  : Player.m_customData  "RP_QS_<questId>"  = "A;obj=n,obj=n" | "C"
    //   * narrative flags   : ZoneSystem global keys "rp_<flag>" (world-wide, same as the rest of the mod)
    //   * reputation        : reputation.rewards rows keyed on the flag "q_<career>_<questId>" (server pays once)
    // =====================================================================
    internal enum QuestState { Locked, Available, Active, Ready, Completed }

    internal sealed class QObjective
    {
        public string id, type, text, npc, item, flag, quest, label;
        public List<string> targets = new List<string>();
        public int count = 1;
        public float x, z, radius;
    }

    internal sealed class QDef
    {
        public string id, chain, name, description, clan, faction, biome, turnIn;
        public int order, tier, minRepTier, repClanPoints;
        public List<string> givers = new List<string>(), careers = new List<string>(), prevQuests = new List<string>();
        public List<string> flags = new List<string>(), notFlags = new List<string>(), next = new List<string>(), rewardFlags = new List<string>();
        public List<QObjective> objectives = new List<QObjective>();
        public Dictionary<string, string> dlg = new Dictionary<string, string>();
        public List<KeyValuePair<string, int>> rewardItems = new List<KeyValuePair<string, int>>();
        public string disabled;   // null = usable; otherwise the reason (shown in logs / journal)
        public bool journal;      // accepted / completed from the menu itself (no NPC needed): used by the generated career quests
        public bool career;       // generated from careers.<id>.calendar
        public int minMonth;      // in-game month (1..12) from which the quest is offered (0 = always)
        public List<KeyValuePair<string, int>> startItems = new List<KeyValuePair<string, int>>();   // handed to the player when the quest is accepted (parcels to deliver)
    }

    internal sealed class QChain { public string id, title, clan, faction, biome; }

    internal static class QuestEngine
    {
        private const string StatePrefix = "RP_QS_";
        private const string RpIdKey = "NorsemanNPC_RP_ID";
        private static readonly string[] ObjTypes = { "kill", "killnpc", "collect", "deliver", "visit", "talk", "flag", "quest", "escort" };
        private static readonly FieldInfo LastHit = typeof(Character).GetField("m_lastHit",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        internal static readonly List<QDef> Ordered = new List<QDef>();
        internal static readonly Dictionary<string, QDef> Quests = new Dictionary<string, QDef>(StringComparer.OrdinalIgnoreCase);
        internal static readonly Dictionary<string, QChain> Chains = new Dictionary<string, QChain>(StringComparer.OrdinalIgnoreCase);
        internal static float InteractRange = 4f;
        internal static int MaxActive = 8;
        internal static bool StrictPrefabs = true;
        // generated career quests (questLines.settings.careerQuests overrides these defaults)
        internal static bool CareerQuestsEnabled = true, CareerMonthGate = true, CareerAutoComplete = false, CareerAutoAccept = false;
        internal static int CareerCoinsPerMonth = 25;
        private static readonly HashSet<string> ReadySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static float _nextCompletionTick;
        private static readonly HashSet<string> ObjSeen = new HashSet<string>(StringComparer.Ordinal);
        private static bool _objSeeded;
        private static bool _prefabsChecked;
        private static float _nextTick;
        private static readonly Dictionary<string, string> SharedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> HintAt = new Dictionary<string, float>();
        private static bool _busy;

        private static ManagerNPCRP.NpcDatabase Db { get { return ManagerNPCRP.Instance != null ? ManagerNPCRP.Instance.Database : null; } }

        // ------------------------------------------------------------------ loading + schema validation
        private static string S(JToken t, string k) { JToken v = t != null ? t[k] : null; return v == null || v.Type == JTokenType.Null ? "" : (string)v; }
        private static int I(JToken t, string k, int def)
        {
            JToken v = t != null ? t[k] : null;
            if (v == null || v.Type == JTokenType.Null) return def;
            return (int)v;
        }
        private static float F(JToken t, string k, float def)
        {
            JToken v = t != null ? t[k] : null;
            if (v == null || v.Type == JTokenType.Null) return def;
            return (float)v;
        }
        private static List<string> L(JToken t, string k)
        {
            List<string> l = new List<string>();
            JArray a = t != null ? t[k] as JArray : null;
            if (a != null) foreach (JToken x in a) l.Add((string)x);
            return l;
        }

        internal static void Load(ManagerNPCRP plugin)
        {
            Ordered.Clear(); Quests.Clear(); Chains.Clear(); _prefabsChecked = false; SharedNames.Clear(); ReadySeen.Clear(); ObjSeen.Clear(); _objSeeded = false;
            try
            {
                ManagerNPCRP.NpcDatabase db = plugin.Database;
                JToken ql;
                if (db == null || db.extra == null || !db.extra.TryGetValue("questLines", out ql) || !(ql is JObject))
                {
                    plugin.Log.LogWarning("[QUEST] No 'questLines' section in the database: quest engine idle.");
                    return;
                }
                if (I(ql, "schemaVersion", 0) != 1) plugin.Log.LogWarning("[QUEST] questLines.schemaVersion is not 1; trying anyway.");
                JToken st = ql["settings"];
                InteractRange = Mathf.Clamp(F(st, "interactRange", 4f), 1.5f, 8f);
                MaxActive = Mathf.Clamp(I(st, "maxActive", 8), 1, 30);
                StrictPrefabs = st == null || st["strictPrefabs"] == null || (bool)st["strictPrefabs"];
                JToken cq = st != null ? st["careerQuests"] : null;
                CareerQuestsEnabled = cq == null || cq["enabled"] == null || (bool)cq["enabled"];
                CareerMonthGate = cq == null || cq["monthGate"] == null || (bool)cq["monthGate"];
                CareerAutoComplete = cq != null && cq["autoComplete"] != null && (bool)cq["autoComplete"];
                CareerAutoAccept = cq != null && cq["autoAccept"] != null && (bool)cq["autoAccept"];
                CareerCoinsPerMonth = Mathf.Max(0, I(cq, "coinsPerMonth", 25));

                JObject chains = ql["chains"] as JObject;
                if (chains != null)
                    foreach (JProperty p in chains.Properties())
                        Chains[p.Name] = new QChain { id = p.Name, title = S(p.Value, "title"), clan = S(p.Value, "clan"), faction = S(p.Value, "faction"), biome = S(p.Value, "biome") };

                JArray arr = ql["quests"] as JArray;
                if (arr == null) { plugin.Log.LogError("[QUEST] questLines.quests is missing or not an array."); return; }
                int bad = 0;
                foreach (JToken jq in arr)
                {
                    QDef q = null;
                    try { q = Parse(jq); }
                    catch (Exception ex) { plugin.Log.LogError("[QUEST] Unreadable quest definition (" + S(jq, "id") + "): " + ex.Message); bad++; continue; }
                    if (Quests.ContainsKey(q.id)) { plugin.Log.LogError("[QUEST] Duplicate quest id '" + q.id + "': second definition ignored."); bad++; continue; }
                    Quests[q.id] = q; Ordered.Add(q);
                }
                foreach (QDef q in Ordered)
                {
                    List<string> errs = Validate(plugin, q);
                    if (errs.Count > 0)
                    {
                        q.disabled = string.Join(" ; ", errs.ToArray());
                        plugin.Log.LogError("[QUEST] " + q.id + " DISABLED: " + q.disabled);
                        bad++;
                    }
                }
                foreach (QDef q in Ordered) if (q.disabled == null && HasCycle(q, new HashSet<string>())) { q.disabled = "prerequisite cycle"; plugin.Log.LogError("[QUEST] " + q.id + " DISABLED: prerequisite cycle"); bad++; }
                BuildCareerQuests(plugin, ql);
                Ordered.Sort((a, b) => a.chain != b.chain ? string.CompareOrdinal(a.chain, b.chain) : a.order.CompareTo(b.order));
                plugin.Log.LogInfo("[QUEST] " + Ordered.Count + " quest(s) loaded, " + bad + " problem(s), chains: " + string.Join(", ", Chains.Keys.ToArray()));
            }
            catch (Exception ex) { plugin.Log.LogError("[QUEST] Load failed: " + ex); }
        }

        private static QDef Parse(JToken j)
        {
            QDef q = new QDef();
            q.id = S(j, "id"); q.chain = S(j, "chain"); q.name = S(j, "name"); q.description = S(j, "description");
            q.clan = S(j, "clan"); q.faction = S(j, "faction"); q.biome = S(j, "biome"); q.turnIn = S(j, "turnIn");
            q.order = I(j, "order", 0); q.tier = I(j, "tier", 0);
            q.givers = L(j, "givers");
            JToken rq = j["requires"];
            q.careers = L(rq, "careers"); q.prevQuests = L(rq, "quests"); q.flags = L(rq, "flags"); q.notFlags = L(rq, "notFlags");
            q.minRepTier = I(rq, "minRepTier", 0);
            q.next = L(j, "next");
            JToken dl = j["dialogue"];
            foreach (string k in new[] { "offer", "accepted", "active", "ready", "done" }) q.dlg[k] = S(dl, k);
            JToken rw = j["rewards"];
            q.repClanPoints = I(rw, "repClanPoints", 0);
            q.rewardFlags = L(rw, "flags");
            JArray items = rw != null ? rw["items"] as JArray : null;
            if (items != null) foreach (JToken it in items) q.rewardItems.Add(new KeyValuePair<string, int>(S(it, "prefab"), I(it, "count", 1)));
            JArray sitems = j["startItems"] as JArray;
            if (sitems != null) foreach (JToken it in sitems) q.startItems.Add(new KeyValuePair<string, int>(S(it, "prefab"), I(it, "count", 1)));
            JArray objs = j["objectives"] as JArray;
            if (objs != null)
                foreach (JToken o in objs)
                {
                    QObjective ob = new QObjective();
                    ob.id = S(o, "id"); ob.type = S(o, "type").ToLowerInvariant(); ob.text = S(o, "text"); ob.npc = S(o, "npc");
                    ob.item = S(o, "item"); ob.flag = S(o, "flag"); ob.quest = S(o, "quest"); ob.label = S(o, "label");
                    ob.targets = L(o, "targets"); ob.count = I(o, "count", 1);
                    ob.x = F(o, "x", 0f); ob.z = F(o, "z", 0f); ob.radius = F(o, "radius", 0f);
                    q.objectives.Add(ob);
                }
            return q;
        }

        private static bool ValidId(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) return false;
            return true;
        }

        private static List<string> Validate(ManagerNPCRP plugin, QDef q)
        {
            List<string> e = new List<string>();
            ManagerNPCRP.NpcDatabase db = plugin.Database;
            if (!ValidId(q.id)) e.Add("invalid id");
            if (string.IsNullOrWhiteSpace(q.name)) e.Add("missing name");
            if (!Chains.ContainsKey(q.chain ?? "")) e.Add("unknown chain '" + q.chain + "'");
            if (!db.clans.ContainsKey(q.clan ?? "")) e.Add("unknown clan '" + q.clan + "'");
            if (!db.factions.ContainsKey(q.faction ?? "")) e.Add("unknown faction '" + q.faction + "'");
            if (q.givers.Count == 0) e.Add("no giver");
            foreach (string g in q.givers) if (!plugin.Npcs.ContainsKey(g)) e.Add("giver '" + g + "' not in npcs");
            if (!plugin.Npcs.ContainsKey(q.turnIn ?? "")) e.Add("turnIn '" + q.turnIn + "' not in npcs");
            foreach (string c in q.careers) if (!db.careers.ContainsKey(c)) e.Add("unknown career '" + c + "'");
            if (q.minRepTier < 0 || q.minRepTier > 3) e.Add("minRepTier must be 0..3");
            if (q.objectives.Count == 0) e.Add("no objective");
            HashSet<string> seen = new HashSet<string>();
            foreach (QObjective o in q.objectives)
            {
                if (!ValidId(o.id) || !seen.Add(o.id)) e.Add("objective id '" + o.id + "' invalid or duplicated");
                if (Array.IndexOf(ObjTypes, o.type) < 0) { e.Add("objective '" + o.id + "' unknown type '" + o.type + "'"); continue; }
                switch (o.type)
                {
                    case "kill": if (o.targets.Count == 0 || o.count < 1) e.Add("kill '" + o.id + "' needs targets and count>=1"); break;
                    case "collect":
                    case "deliver": if (string.IsNullOrEmpty(o.item) || o.count < 1) e.Add(o.type + " '" + o.id + "' needs item and count>=1"); break;
                    case "visit": if (o.radius <= 0f) e.Add("visit '" + o.id + "' needs radius>0"); break;
                    case "escort": if (o.radius <= 0f || !plugin.Npcs.ContainsKey(o.npc ?? "")) e.Add("escort '" + o.id + "' needs a known npc and a destination radius>0"); break;
                    case "talk": if (!plugin.Npcs.ContainsKey(o.npc ?? "")) e.Add("talk '" + o.id + "' unknown npc '" + o.npc + "'"); break;
                    case "flag": if (string.IsNullOrEmpty(o.flag)) e.Add("flag '" + o.id + "' needs flag"); break;
                    case "quest": if (string.IsNullOrEmpty(o.quest)) e.Add("quest '" + o.id + "' needs quest"); break;
                }
            }
            foreach (string p in q.prevQuests) if (!Quests.ContainsKey(p)) e.Add("unknown prerequisite quest '" + p + "'");
            foreach (string n in q.next) if (!Quests.ContainsKey(n)) e.Add("unknown next quest '" + n + "'");
            foreach (KeyValuePair<string, int> it in q.rewardItems) if (string.IsNullOrEmpty(it.Key) || it.Value < 1) e.Add("invalid reward item");
            if (q.repClanPoints > 0)
            {
                ReputationSettings r = db.reputation;
                bool anyRow = false;
                foreach (RepReward rw in r.rewards) if (string.Equals(rw.flag, RewardFlag(q, "renegat"), StringComparison.OrdinalIgnoreCase)) { anyRow = true; break; }
                if (!anyRow) plugin.Log.LogWarning("[QUEST] " + q.id + ": repClanPoints=" + q.repClanPoints + " but no reputation.rewards row for flag '" + RewardFlag(q, "renegat") + "' (no reputation will be paid).");
            }
            return e;
        }

        private static bool HasCycle(QDef q, HashSet<string> path)
        {
            if (!path.Add(q.id)) return true;
            foreach (string p in q.prevQuests) { QDef pq; if (Quests.TryGetValue(p, out pq) && HasCycle(pq, new HashSet<string>(path))) return true; }
            return false;
        }

        internal static string RewardFlag(QDef q, string career) { return ("q_" + career + "_" + q.id).ToLowerInvariant(); }

        // ------------------------------------------------------------------ runtime reference check (needs a loaded world)
        internal static string CheckPrefabs(bool force)
        {
            if (!force && _prefabsChecked) return null;
            if (ObjectDB.instance == null || ZNetScene.instance == null) return "world not ready";
            _prefabsChecked = true;
            int bad = 0, ok = 0;
            foreach (QDef q in Ordered)
            {
                if (q.disabled != null && !q.disabled.StartsWith("prefab:")) continue;
                List<string> miss = new List<string>();
                foreach (QObjective o in q.objectives)
                {
                    if (o.type == "kill") foreach (string t in o.targets) if (ZNetScene.instance.GetPrefab(t) == null) miss.Add("creature " + t);
                    if ((o.type == "collect" || o.type == "deliver") && ObjectDB.instance.GetItemPrefab(o.item) == null) miss.Add("item " + o.item);
                }
                foreach (KeyValuePair<string, int> it in q.rewardItems)
                    if (ObjectDB.instance.GetItemPrefab(it.Key) == null) ManagerNPCRP.Instance.Log.LogWarning("[QUEST] " + q.id + ": reward item '" + it.Key + "' unknown (reward skipped).");
                if (miss.Count > 0)
                {
                    bad++;
                    string why = "prefab: " + string.Join(", ", miss.ToArray()) + " not found in this game version - fix the name in npc_database.json";
                    ManagerNPCRP.Instance.Log.LogError("[QUEST] " + q.id + " " + (StrictPrefabs ? "DISABLED" : "WARNING") + " " + why);
                    if (StrictPrefabs) q.disabled = why;
                    else q.disabled = null;
                }
                else { ok++; if (q.disabled != null) q.disabled = null; }
            }
            string res = "prefab check: " + ok + " quest(s) OK, " + bad + " with unknown references";
            ManagerNPCRP.Instance.Log.LogInfo("[QUEST] " + res);
            return res;
        }

        // ------------------------------------------------------------------ per-player state
        private static string Raw(Player p, string id) { string v; return p != null && p.m_customData.TryGetValue(StatePrefix + id, out v) ? v : ""; }
        internal static bool IsCompleted(Player p, string id) { return Raw(p, id) == "C"; }
        internal static bool IsActive(Player p, string id) { return Raw(p, id).StartsWith("A;", StringComparison.Ordinal); }

        private static Dictionary<string, int> Prog(Player p, string id)
        {
            Dictionary<string, int> d = new Dictionary<string, int>();
            string raw = Raw(p, id);
            if (!raw.StartsWith("A;", StringComparison.Ordinal)) return d;
            foreach (string part in raw.Substring(2).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('='); int n;
                if (eq > 0 && int.TryParse(part.Substring(eq + 1), out n)) d[part.Substring(0, eq)] = n;
            }
            return d;
        }
        private static void SaveProg(Player p, string id, Dictionary<string, int> d)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in d) parts.Add(kv.Key + "=" + kv.Value);
            p.m_customData[StatePrefix + id] = "A;" + string.Join(",", parts.ToArray());
        }

        internal static string SharedName(string prefab)
        {
            string s;
            if (SharedNames.TryGetValue(prefab, out s)) return s;
            if (ObjectDB.instance == null) return null;
            GameObject go = ObjectDB.instance.GetItemPrefab(prefab);
            ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
            if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) return null;
            SharedNames[prefab] = drop.m_itemData.m_shared.m_name;
            return drop.m_itemData.m_shared.m_name;
        }
        internal static int OwnedCount(Player p, string prefab) { return CountItem(p, prefab); }
        private static int CountItem(Player p, string prefab)
        {
            string shared = SharedName(prefab);
            return shared == null || p.GetInventory() == null ? 0 : p.GetInventory().CountItems(shared);
        }

        internal static void ObjStatus(Player p, QDef q, QObjective o, Dictionary<string, int> prog, out int have, out int need)
        {
            need = 1; have = 0;
            switch (o.type)
            {
                case "kill":
                case "killnpc": need = o.count; prog.TryGetValue(o.id, out have); break;
                case "collect": need = o.count; have = CountItem(p, o.item); break;
                case "deliver":
                    if (!string.IsNullOrEmpty(o.npc)) { int dv; prog.TryGetValue(o.id, out dv); need = 1; have = dv > 0 ? 1 : 0; }   // handed over to the NPC: items already consumed
                    else { need = o.count; have = CountItem(p, o.item); }                                                           // consumed when the quest is handed in
                    break;
                case "visit":
                case "talk": { int v; prog.TryGetValue(o.id, out v); have = (v > 0 || (!string.IsNullOrEmpty(o.flag) && DialogueStages.FlagOn(o.flag))) ? 1 : 0; break; }
                case "flag": have = (DialogueStages.FlagOn(o.flag) || AllFlagsOn(o.targets)) ? 1 : 0; break;
                case "quest": have = IsCompleted(p, o.quest) ? 1 : 0; break;
                case "escort": { int ev; prog.TryGetValue(o.id, out ev); have = ev > 0 ? 1 : 0; break; }
            }
            if (have > need) have = need;
        }
        private static bool AllFlagsOn(List<string> flags)
        {
            if (flags == null || flags.Count == 0) return false;
            foreach (string f in flags) if (!DialogueStages.FlagOn(f)) return false;
            return true;
        }
        private static bool AllDone(Player p, QDef q, Dictionary<string, int> prog)
        {
            foreach (QObjective o in q.objectives) { int h, n; ObjStatus(p, q, o, prog, out h, out n); if (h < n) return false; }
            return true;
        }

        /// <summary>Access rules, each one independent: career list, prerequisite quests, story flags, then the reputation of the quest's clan/faction.</summary>
        internal static QuestState Evaluate(Player p, QDef q, out string reason)
        {
            reason = "";
            if (q.disabled != null) { reason = q.disabled; return QuestState.Locked; }
            string raw = Raw(p, q.id);
            if (raw == "C") return QuestState.Completed;
            if (raw.StartsWith("A;", StringComparison.Ordinal)) return AllDone(p, q, Prog(p, q.id)) ? QuestState.Ready : QuestState.Active;
            string career = ReputationManager.LocalCareer();
            if (q.career && !ReputationManager.HasChosenCareer())
            { reason = "aucune allégeance : faites-vous attribuer une carrière"; return QuestState.Locked; }
            if (q.careers.Count > 0 && !q.careers.Any(c => string.Equals(c, career, StringComparison.OrdinalIgnoreCase)))
            { reason = "réservée à d'autres carrières"; return QuestState.Locked; }
            foreach (string pq in q.prevQuests)
                if (!IsCompleted(p, pq)) { QDef d; reason = "terminer d'abord : " + (Quests.TryGetValue(pq, out d) ? d.name : pq); return QuestState.Locked; }
            foreach (string f in q.flags) if (!DialogueStages.FlagOn(f)) { reason = "événement requis : " + f; return QuestState.Locked; }
            foreach (string f in q.notFlags) if (DialogueStages.FlagOn(f)) { reason = "plus disponible (" + f + ")"; return QuestState.Locked; }
            if (q.minMonth > 0 && CareerMonthGate)
            {
                int m = CurrentMonth();
                if (m < q.minMonth) { reason = "disponible au mois " + q.minMonth + " (mois actuel : " + m + ")"; return QuestState.Locked; }
            }
            if (QuestTier(q) < q.minRepTier)
            { reason = "réputation « " + TierName(q.minRepTier) + " » requise auprès de " + q.clan; return QuestState.Locked; }
            return QuestState.Available;
        }
        /// <summary>Same rule as ReputationManager.TierFor for a city clan: best of the clan tier and (faction tier - 1).</summary>
        internal static int QuestTier(QDef q)
        {
            return Mathf.Max(ReputationManager.TierFromFlags(q.clan), Mathf.Max(0, ReputationManager.FactionTierFromFlags(q.faction) - 1));
        }
        internal static string TierName(int t)
        {
            ReputationSettings r = Db != null ? Db.reputation : null;
            return r != null && r.tierNames.Count > 0 ? r.tierNames[Mathf.Min(t, r.tierNames.Count - 1)] : t.ToString();
        }
        internal static string NpcName(string id)
        {
            ManagerNPCRP.NpcDefinition n;
            return ManagerNPCRP.Instance != null && ManagerNPCRP.Instance.Npcs.TryGetValue(id, out n) ? n.identity.name : id;
        }
        // ------------------------------------------------------------------ status / labels / reputation helpers
        /// <summary>In-game month (1..12+) derived from the world day, same month length as the reputation drift.</summary>
        internal static int CurrentMonth()
        {
            int days = EnvMan.instance != null ? EnvMan.instance.GetDay() : 0;
            int md = Db != null && Db.reputation != null ? Math.Max(1, Db.reputation.monthDays) : 30;
            return days / md + 1;
        }

        internal static bool IsMonthLocked(QDef q) { return q.minMonth > 0 && CareerMonthGate && CurrentMonth() < q.minMonth; }

        /// <summary>True when the quest is not meant for this player (another career, or career quests before any career is assigned).</summary>
        internal static bool HiddenForPlayer(QDef q)
        {
            if (q.career && !ReputationManager.HasChosenCareer()) return true;
            string career = ReputationManager.LocalCareer();
            return q.careers.Count > 0 && !q.careers.Any(c => string.Equals(c, career, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A locked quest is listed unless it only waits for an earlier quest of its chain (no spoilers).</summary>
        internal static bool ShowLocked(Player lp, QDef q)
        {
            foreach (string p in q.prevQuests) if (!IsCompleted(lp, p)) return false;
            return true;
        }

        internal static string GiverLabel(QDef q)
        {
            return q.givers.Count > 0 ? string.Join(" / ", q.givers.Select(g => NpcName(g)).ToArray()) : "Journal";
        }
        internal static string TurnInLabel(QDef q) { return q.journal || string.IsNullOrEmpty(q.turnIn) ? "Journal" : NpcName(q.turnIn); }

        internal static string StatusTag(QDef q, QuestState s)
        {
            switch (s)
            {
                case QuestState.Ready: return "À RENDRE";
                case QuestState.Active: return "EN COURS";
                case QuestState.Completed: return "FAITE";
                case QuestState.Available: return "DISPONIBLE";
                default: return IsMonthLocked(q) ? "À VENIR" : "VERROUILLÉE";
            }
        }

        internal static Color StatusColor(QuestState s)
        {
            switch (s)
            {
                case QuestState.Ready: return new Color(0.55f, 1f, 0.55f);
                case QuestState.Active: return new Color(1f, 0.82f, 0.35f);
                case QuestState.Available: return new Color(0.6f, 0.85f, 1f);
                case QuestState.Completed: return new Color(0.65f, 0.65f, 0.65f);
                default: return new Color(0.8f, 0.55f, 0.5f);
            }
        }

        /// <summary>Reputation rows (reputation.rewards) that this quest triggers for the career: its goal flags and its own turn-in flag.</summary>
        internal static string RepGainText(QDef q, string career) { return RepText(q, career, 1); }
        internal static string RepLossText(QDef q, string career) { return RepText(q, career, -1); }

        private static string RepText(QDef q, string career, int sign)
        {
            ReputationSettings r = Db != null ? Db.reputation : null;
            if (r == null) return "";
            HashSet<string> flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (QObjective o in q.objectives) if (o.type == "flag" && !string.IsNullOrEmpty(o.flag)) flags.Add(o.flag);
            foreach (string f in q.rewardFlags) flags.Add(f);
            flags.Add(RewardFlag(q, career));
            Dictionary<string, int> sum = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (RepReward rw in r.rewards)
            {
                if (!flags.Contains(rw.flag ?? "")) continue;
                if (sign > 0 && rw.points <= 0) continue;
                if (sign < 0 && rw.points >= 0) continue;
                if (!string.IsNullOrEmpty(rw.career) && !string.Equals(rw.career, career, StringComparison.OrdinalIgnoreCase)) continue;
                string target = !string.IsNullOrEmpty(rw.clan) ? rw.clan : rw.faction;
                if (string.IsNullOrEmpty(target)) continue;
                int n; sum.TryGetValue(target, out n); sum[target] = n + rw.points;
            }
            return string.Join(", ", sum.Select(kv => kv.Key + " " + (kv.Value >= 0 ? "+" : "") + kv.Value).ToArray());
        }

        /// <summary>Points already granted by quests/events whose flag is set (clan rows when clan != null, otherwise direct faction rows).</summary>
        internal static int QuestRepPoints(string clan, string faction, string career)
        {
            ReputationSettings r = Db != null ? Db.reputation : null;
            if (r == null) return 0;
            int sum = 0;
            foreach (RepReward rw in r.rewards)
            {
                if (string.IsNullOrEmpty(rw.flag) || !DialogueStages.RawOn(rw.flag)) continue;
                if (!string.IsNullOrEmpty(rw.career) && !string.Equals(rw.career, career, StringComparison.OrdinalIgnoreCase)) continue;
                bool match = clan != null
                    ? string.Equals(rw.clan, clan, StringComparison.OrdinalIgnoreCase)
                    : string.IsNullOrEmpty(rw.clan) && string.Equals(rw.faction, faction, StringComparison.OrdinalIgnoreCase);
                if (match) sum += rw.points;
            }
            return sum;
        }

        // ------------------------------------------------------------------ career quests (generated from careers.<id>.calendar)
        /// <summary>The lore notes are written for the game master ("Les PJ ont ..."): speak to the player instead.</summary>
        private static string Voice(string d)
        {
            if (string.IsNullOrEmpty(d)) return d ?? "";
            string[][] map =
            {
                new[] { "Les PJ ont ", "Vous avez " }, new[] { "Les PJ savent ", "Vous savez " },
                new[] { "Les PJ soupçonnent ", "Vous soupçonnez " }, new[] { "Les PJ lient ", "Vous liez " },
                new[] { "Les PJ remarquent ", "Vous remarquez " }, new[] { "Les PJ couvrent ", "Vous couvrez " },
                new[] { "Les PJ aident ", "Vous aidez " }, new[] { "Les PJ rejoignent ", "Vous rejoignez " },
                new[] { "Les PJ renseignent ", "Vous renseignez " }, new[] { "Les PJ ", "Vous " }
            };
            foreach (string[] m in map)
                if (d.StartsWith(m[0], StringComparison.Ordinal)) return m[1] + d.Substring(m[0].Length);
            return d;
        }

        /// <summary>
        /// One quest per calendar month of every career: its objectives are the month's goal flags (story events that
        /// the world / game master / conversations set). Reputation is already paid by reputation.rewards on those flags.
        /// </summary>
        private static void BuildCareerQuests(ManagerNPCRP plugin, JToken ql)
        {
            if (!CareerQuestsEnabled) return;
            JToken objDefs = ql != null ? ql["careerObjectives"] : null;
            ManagerNPCRP.NpcDatabase db = plugin.Database;
            if (db == null || db.careers == null) return;

            Dictionary<string, string> info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> setBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JToken lore;
            if (db.extra != null && db.extra.TryGetValue("lore", out lore) && lore != null)
            {
                JObject threads = lore["threads"] as JObject;
                if (threads != null)
                    foreach (JProperty tp in threads.Properties())
                    {
                        string title = S(tp.Value, "title");
                        JArray fl = tp.Value["flags"] as JArray;
                        if (fl == null) continue;
                        foreach (JToken f in fl)
                        {
                            string fid = S(f, "id");
                            if (fid.Length > 0) { info[fid] = (title.Length > 0 ? title + " — " : "") + Voice(S(f, "desc")); setBy[fid] = S(f, "setBy"); }
                        }
                    }
            }

            int made = 0;
            foreach (KeyValuePair<string, CareerDefinition> kv in db.careers)
            {
                CareerDefinition cd = kv.Value;
                JToken calTok;
                JArray cal = cd != null && cd.extra != null && cd.extra.TryGetValue("calendar", out calTok) ? calTok as JArray : null;
                if (cal == null || cal.Count == 0) continue;

                string chainId = "career_" + kv.Key;
                Chains[chainId] = new QChain
                {
                    id = chainId,
                    title = "Carrière — " + (string.IsNullOrEmpty(cd.name) ? kv.Key : cd.name),
                    clan = cd.hubClans.Count > 0 ? cd.hubClans[0] : "",
                    faction = cd.faction ?? "",
                    biome = ""
                };
                List<QDef> chainQuests = new List<QDef>();
                foreach (JToken m in cal)
                {
                    int month = I(m, "month", chainQuests.Count + 1);
                    List<string> goals = L(m, "goals"), clans = L(m, "clans");
                    JArray extras = m["objectives"] as JArray;
                    if (goals.Count == 0 && (extras == null || extras.Count == 0)) continue;

                    QDef q = new QDef();
                    q.id = "CAR_" + kv.Key + "_" + month.ToString("00");
                    if (Quests.ContainsKey(q.id)) continue;
                    q.chain = chainId; q.order = month; q.tier = month; q.minMonth = month;
                    q.name = "Mois " + month + " — " + S(m, "title");
                    string note = S(m, "note");
                    q.description = (note.Length > 0 ? note + " " : "") +
                                    (clans.Count > 0 ? "Régions concernées : " + string.Join(", ", clans.ToArray()) + "." : "");
                    q.clan = "";
                    foreach (string c in clans) if (db.clans.ContainsKey(c)) { q.clan = c; break; }
                    if (q.clan.Length == 0 && cd.hubClans.Count > 0) q.clan = cd.hubClans[0];
                    q.faction = cd.faction ?? ""; q.biome = ""; q.turnIn = "";
                    q.careers.Add(kv.Key); q.journal = true; q.career = true;

                    int n = 0;
                    foreach (string g in goals)
                    {
                        n++;
                        JToken e = objDefs != null ? objDefs[g] : null;
                        QObjective o = e != null ? BuildObjective(e, g, "g" + n, db, setBy) : null;
                        if (o == null)
                        {
                            string tx;
                            if (!info.TryGetValue(g, out tx)) tx = g;
                            o = new QObjective { id = "g" + n, type = "flag", flag = g, text = tx + " (validé par le MJ)" };
                        }
                        q.objectives.Add(o);
                    }
                    // free objectives written straight in the calendar month (kill / killnpc / deliver / collect / talk / visit ...)
                    if (extras != null)
                    {
                        int xn = 0;
                        foreach (JToken x in extras)
                        {
                            QObjective xo = BuildObjective(x, S(x, "flag"), "x" + (++xn), db, setBy);
                            if (xo != null) q.objectives.Add(xo);
                            else plugin.Log.LogWarning("[QUEST] " + q.id + ": calendar objective #" + xn + " ignored (incomplete or unknown type).");
                        }
                    }
                    foreach (string k in new[] { "offer", "accepted", "active", "ready", "done" }) q.dlg[k] = "";
                    JToken rwd = m["rewards"];
                    int coins = rwd != null && rwd["coins"] != null ? I(rwd, "coins", 0) : CareerCoinsPerMonth * month;
                    if (coins > 0) q.rewardItems.Add(new KeyValuePair<string, int>("Coins", coins));
                    JObject rwItems = rwd != null ? rwd["items"] as JObject : null;
                    if (rwItems != null)
                        foreach (JProperty ip in rwItems.Properties())
                            q.rewardItems.Add(new KeyValuePair<string, int>(ip.Name, Mathf.Max(1, (int)ip.Value)));
                    Quests[q.id] = q; Ordered.Add(q); chainQuests.Add(q); made++;
                }
                for (int i = 0; i + 1 < chainQuests.Count; i++) chainQuests[i].next.Add(chainQuests[i + 1].id);
            }
            plugin.Log.LogInfo("[QUEST] " + made + " career quest(s) generated from careers.*.calendar");
        }

        private static List<string> IdList(JToken t)
        {
            List<string> l = new List<string>();
            JArray a = t as JArray;
            if (a != null) { foreach (JToken x in a) if (x != null) l.Add((string)x); }
            else if (t != null && t.Type == JTokenType.String) l.Add((string)t);
            return l;
        }

        /// <summary>
        /// One calendar objective from JSON. type: talk | visit | auto | kill | killnpc | deliver | collect | quest | flag.
        /// Returns null when the entry is unusable (flag objective without a flag).
        /// </summary>
        private static QObjective BuildObjective(JToken e, string flag, string id, ManagerNPCRP.NpcDatabase db, Dictionary<string, string> setBy)
        {
            string type = S(e, "type");
            QObjective o = new QObjective { id = id, type = "flag", flag = flag ?? "", text = S(e, "text"), count = Math.Max(1, I(e, "count", 1)) };
            switch (type)
            {
                case "talk":
                    o.targets.AddRange(IdList(e["npc"]));
                    if (o.targets.Count > 0) { o.type = "talk"; o.npc = o.targets[0]; }
                    break;
                case "visit":
                    {
                        CityZone cz; string clan = S(e, "clan");
                        if (clan.Length > 0 && db.cityZones.TryGetValue(clan, out cz))
                        { o.type = "visit"; o.x = cz.x; o.z = cz.z; o.radius = Mathf.Max(30f, F(e, "radius", db.reputation.cityRadius)); }
                        else if (e["x"] != null && e["z"] != null)
                        { o.type = "visit"; o.x = F(e, "x", 0f); o.z = F(e, "z", 0f); o.radius = Mathf.Max(5f, F(e, "radius", 30f)); }
                        break;
                    }
                case "auto":
                    foreach (string af in L(e, "all")) o.targets.Add(af);
                    break;
                case "kill":                      // creatures, by prefab name (Greydwarf, Draugr, Boar ...)
                    o.targets.AddRange(L(e, "targets"));
                    if (o.targets.Count > 0) o.type = "kill";
                    break;
                case "killnpc":                   // RP NPCs by id, and/or generic citizens of a clan
                    o.targets.AddRange(IdList(e["npc"]));
                    foreach (string c in L(e, "clans")) o.targets.Add("GEN:" + c);
                    if (o.targets.Count > 0) o.type = "killnpc";
                    break;
                case "deliver":                   // items are REMOVED from the inventory (at the NPC if 'npc' is given, else when the quest is handed in)
                case "collect":                   // items must be carried, nothing is removed
                    o.item = S(e, "item");
                    if (o.item.Length > 0)
                    {
                        o.type = type;
                        if (type == "deliver") { o.targets.AddRange(IdList(e["npc"])); if (o.targets.Count > 0) o.npc = o.targets[0]; }
                    }
                    break;
                case "quest":
                    o.quest = S(e, "quest");
                    if (o.quest.Length > 0) o.type = "quest";
                    break;
            }
            if (o.type == "flag" && o.flag.Length == 0) return null;

            if (o.text.Length == 0)
            {
                switch (o.type)
                {
                    case "kill": o.text = "Éliminez " + o.count + " × " + string.Join(" / ", o.targets.ToArray()); break;
                    case "killnpc":
                        o.text = "Éliminez " + string.Join(" / ", o.targets.Select(t => t.StartsWith("GEN:", StringComparison.Ordinal) ? "des citoyens de " + t.Substring(4) : NpcName(t)).ToArray()) +
                                 (o.count > 1 ? " (" + o.count + ")" : "");
                        break;
                    case "deliver": o.text = "Remettez " + o.count + " × " + o.item + (string.IsNullOrEmpty(o.npc) ? "" : " à " + NpcName(o.npc)); break;
                    case "collect": o.text = "Rapportez " + o.count + " × " + o.item; break;
                    case "talk": o.text = "Parlez à " + NpcName(o.npc); break;
                    case "visit": o.text = "Rendez-vous sur place"; break;
                    case "quest": o.text = "Terminez la quête " + o.quest; break;
                    default: o.text = o.flag; break;
                }
            }
            if (o.type == "flag" && o.targets.Count == 0)
            {
                string by; setBy.TryGetValue(o.flag, out by);
                if (string.IsNullOrEmpty(by) || by == "mj") o.text += " (validé par le MJ)";
            }
            return o;
        }

        /// <summary>Live completion: announces finished quests and, for menu quests, completes them (and pays) by itself.</summary>
        private static void TickCompletion()
        {
            Player lp = Player.m_localPlayer;
            if (lp == null || _busy || Time.time < _nextCompletionTick) return;
            _nextCompletionTick = Time.time + 1f;
            foreach (QDef q in new List<QDef>(Ordered))
            {
                if (q.disabled != null) continue;
                string why;
                if (q.journal && CareerAutoAccept && Evaluate(lp, q, out why) == QuestState.Available)
                {
                    if (Accept(lp, q, null).Length == 0) lp.Message(MessageHud.MessageType.TopLeft, "Nouvelle quête : " + q.name);
                    continue;
                }
                if (!IsActive(lp, q.id)) continue;
                SyncObjectiveFlags(lp, q);
                NotifyObjectives(lp, q);
                if (!AllDone(lp, q, Prog(lp, q.id))) { ReadySeen.Remove(q.id); continue; }
                if (q.journal && CareerAutoComplete)
                {
                    string line; string err = TurnIn(lp, q, null, out line);
                    if (err.Length > 0) { LogDbg(q.id + " auto-complete refused: " + err); if (ReadySeen.Add(q.id)) AnnounceIfReady(lp, q); }
                    continue;
                }
                if (ReadySeen.Add(q.id)) AnnounceIfReady(lp, q);
            }
            _objSeeded = true;
        }

        /// <summary>An objective validated in game sets its world story flag (unlocks dialogues, feeds the other quests).</summary>
        private static void SyncObjectiveFlags(Player lp, QDef q)
        {
            if (!q.career) return;
            Dictionary<string, int> prog = Prog(lp, q.id);
            foreach (QObjective o in q.objectives)
            {
                if (string.IsNullOrEmpty(o.flag) || DialogueStages.FlagOn(o.flag)) continue;
                int h, n; ObjStatus(lp, q, o, prog, out h, out n);
                if (h >= n) SetFlag(o.flag);
            }
        }

        /// <summary>Tells the player when an objective gets validated (story flag set by the game master, a conversation...).</summary>
        private static void NotifyObjectives(Player lp, QDef q)
        {
            Dictionary<string, int> prog = Prog(lp, q.id);
            int done = 0;
            List<QObjective> fresh = new List<QObjective>();
            foreach (QObjective o in q.objectives)
            {
                int h, n; ObjStatus(lp, q, o, prog, out h, out n);
                if (h < n) continue;
                done++;
                int pv;
                bool viaProgress = (o.type == "talk" || o.type == "visit" || o.type == "kill" || o.type == "killnpc" || (o.type == "deliver" && !string.IsNullOrEmpty(o.npc))) && prog.TryGetValue(o.id, out pv) && pv > 0;   // already announced by OnTalk / TickVisits
                if (ObjSeen.Add(q.id + "|" + o.id) && _objSeeded && !viaProgress) fresh.Add(o);
            }
            foreach (QObjective o in fresh)
                lp.Message(MessageHud.MessageType.TopLeft, "Objectif validé (" + done + "/" + q.objectives.Count + ") : " + o.text);
        }

        internal static string Line(QDef q, QuestState s)
        {
            string k = s == QuestState.Available ? "offer" : s == QuestState.Active ? "active" : s == QuestState.Ready ? "ready" : "done";
            string v; return q.dlg.TryGetValue(k, out v) ? v : "";
        }

        // ------------------------------------------------------------------ player actions
        internal static string Accept(Player p, QDef q, string npcId)
        {
            if (_busy) return "";
            string why; QuestState st = Evaluate(p, q, out why);
            if (st != QuestState.Available) return st == QuestState.Locked ? "Quête verrouillée : " + why : "Quête déjà " + (st == QuestState.Completed ? "terminée." : "en cours.");
            if (!q.journal && !q.givers.Contains(npcId)) return "Ce n'est pas le bon interlocuteur : " + string.Join(" ou ", q.givers.Select(g => NpcName(g)).ToArray()) + ".";
            int active = Ordered.Count(x => IsActive(p, x.id));
            if (active >= MaxActive) return "Trop de quêtes actives (" + MaxActive + " maximum).";
            Dictionary<string, int> prog = new Dictionary<string, int>();
            foreach (QObjective o in q.objectives) if (o.type == "kill" || o.type == "killnpc" || o.type == "visit" || o.type == "talk" || o.type == "escort" || (o.type == "deliver" && !string.IsNullOrEmpty(o.npc))) prog[o.id] = 0;
            SaveProg(p, q.id, prog);
            foreach (KeyValuePair<string, int> si in q.startItems)
            {
                GiveItem(p, si.Key, si.Value);
                p.Message(MessageHud.MessageType.TopLeft, "On vous confie : " + si.Value + " × " + si.Key);
            }
            LogDbg("accepted " + q.id);
            return "";
        }

        internal static string Abandon(Player p, QDef q)
        {
            if (!IsActive(p, q.id)) return "Cette quête n'est pas active.";
            p.m_customData.Remove(StatePrefix + q.id);
            ReadySeen.Remove(q.id);
            foreach (QObjective o in q.objectives)
            {
                ObjSeen.Remove(q.id + "|" + o.id);
                if (o.type == "escort") ReleaseEscort(q.id + "|" + o.id, o, p);
            }
            RecallStartItems(p, q);
            LogDbg("abandoned " + q.id);
            return "";
        }

        /// <summary>All-or-nothing: verify every objective, verify items, remove items, only then mark completed and pay.</summary>
        internal static string TurnIn(Player p, QDef q, string npcId, out string doneLine)
        {
            doneLine = "";
            if (_busy) return "";
            _busy = true;
            try
            {
                string why; QuestState st = Evaluate(p, q, out why);
                if (st == QuestState.Completed) return "Déjà rendue.";
                if (st != QuestState.Ready) return "Les objectifs ne sont pas tous accomplis.";
                if (!q.journal && !string.Equals(q.turnIn, npcId, StringComparison.OrdinalIgnoreCase)) return "À rendre auprès de " + NpcName(q.turnIn) + ".";
                // aggregate required items per prefab (two objectives may need the same item)
                Dictionary<string, int> need = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (QObjective o in q.objectives)
                    if (o.type == "deliver" && string.IsNullOrEmpty(o.npc)) { int n; need.TryGetValue(o.item, out n); need[o.item] = n + o.count; }
                foreach (KeyValuePair<string, int> kv in need)
                    if (CountItem(p, kv.Key) < kv.Value) return "Il vous manque des objets : " + kv.Value + " × " + kv.Key + ".";
                List<KeyValuePair<string, int>> removed = new List<KeyValuePair<string, int>>();
                foreach (KeyValuePair<string, int> kv in need)
                {
                    if (RemoveItems(p, kv.Key, kv.Value)) removed.Add(kv);
                    else
                    {
                        foreach (KeyValuePair<string, int> r in removed) GiveItem(p, r.Key, r.Value);   // rollback
                        ManagerNPCRP.Instance.Log.LogError("[QUEST] " + q.id + ": item removal failed for " + kv.Key + ", turn-in cancelled and items restored.");
                        return "Remise impossible pour le moment (objets restitués).";
                    }
                }
                // state first: a reload after this point can never pay twice
                p.m_customData[StatePrefix + q.id] = "C";
                string career = ReputationManager.LocalCareer();
                foreach (string f in q.rewardFlags) SetFlag(f);
                string rwFlag = RewardFlag(q, career);
                if (HasRewardRows(rwFlag)) SetFlag(rwFlag);          // a world flag only when reputation rows are attached to it
                foreach (KeyValuePair<string, int> it in q.rewardItems) GiveItem(p, it.Key, it.Value);
                doneLine = Line(q, QuestState.Completed);
                ReadySeen.Remove(q.id);
                p.Message(MessageHud.MessageType.Center, "Quête terminée : " + q.name);
                p.Message(MessageHud.MessageType.TopLeft, "Journal mis à jour : " + q.name);
                List<string> got = new List<string>();
                foreach (KeyValuePair<string, int> it in q.rewardItems) got.Add(it.Value + " × " + (it.Key == "Coins" ? "pièces" : it.Key));
                if (got.Count > 0) p.Message(MessageHud.MessageType.TopLeft, "Récompenses : " + string.Join(", ", got.ToArray()));
                string gain = RepGainText(q, career), loss = RepLossText(q, career);
                if (gain.Length > 0) p.Message(MessageHud.MessageType.TopLeft, "Réputation : " + gain);
                if (loss.Length > 0) p.Message(MessageHud.MessageType.TopLeft, "Réputation perdue : " + loss);
                if (gain.Length > 0 || loss.Length > 0) p.Message(MessageHud.MessageType.TopLeft, "(versée dans quelques secondes par le serveur)");
                foreach (string n in q.next)
                {
                    QDef nq; string r2;
                    if (!Quests.TryGetValue(n, out nq)) continue;
                    QuestState ns = Evaluate(p, nq, out r2);
                    if (ns == QuestState.Available) p.Message(MessageHud.MessageType.TopLeft, "Nouvelle quête disponible : " + nq.name + " (" + GiverLabel(nq) + ")");
                    else if (ns == QuestState.Locked && IsMonthLocked(nq)) p.Message(MessageHud.MessageType.TopLeft, "Prochaine étape : " + nq.name + " — " + r2);
                }
                if (q.career && CampaignDone(p, career))
                    p.Message(MessageHud.MessageType.Center, "Campagne de carrière accomplie !");
                LogDbg("completed " + q.id + " career=" + career);
                return "";
            }
            finally { _busy = false; }
        }

        /// <summary>True when every month of the career's generated campaign has been handed in by this player.</summary>
        /// <summary>Allegiance change: active quests reserved for other careers are abandoned (completed ones stay completed).</summary>
        internal static void DropCareerQuests(Player p, string newCareer)
        {
            foreach (QDef q in new List<QDef>(Ordered))
            {
                if (q.careers.Count == 0 || !IsActive(p, q.id)) continue;
                if (q.careers.Any(c => string.Equals(c, newCareer, StringComparison.OrdinalIgnoreCase))) continue;
                Abandon(p, q);
            }
        }

        internal static bool CampaignDone(Player p, string career)
        {
            int total = 0, done = 0;
            foreach (QDef x in Ordered)
            {
                if (!x.career || !x.careers.Any(c => string.Equals(c, career, StringComparison.OrdinalIgnoreCase))) continue;
                total++;
                if (IsCompleted(p, x.id)) done++;
            }
            return total > 0 && done == total;
        }

        private static bool HasRewardRows(string flag)
        {
            ReputationSettings r = Db != null ? Db.reputation : null;
            if (r == null || r.rewards == null) return false;
            foreach (RepReward rw in r.rewards)
                if (string.Equals(rw.flag, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void SetFlag(string flag)
        {
            if (string.IsNullOrEmpty(flag) || ZoneSystem.instance == null) return;
            if (!DialogueStages.RawOn(flag)) ZoneSystem.instance.SetGlobalKey("rp_" + flag);
        }

        // reflection keeps the mod compiling across Valheim versions that changed these signatures
        private static object[] FillArgs(ParameterInfo[] ps, params object[] first)
        {
            object[] a = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (i < first.Length) a[i] = first[i];
                else if (ps[i].HasDefaultValue && ps[i].DefaultValue != null && ps[i].DefaultValue != DBNull.Value) a[i] = ps[i].DefaultValue;
                else if (ps[i].ParameterType == typeof(int)) a[i] = (ps[i].Name ?? "").ToLowerInvariant().Contains("quality") ? 1 : 0;
                else if (ps[i].ParameterType == typeof(bool)) a[i] = false;
                else if (ps[i].ParameterType == typeof(long)) a[i] = 0L;
                else if (ps[i].ParameterType == typeof(string)) a[i] = "";
                else a[i] = null;
            }
            return a;
        }
        private static bool RemoveItems(Player p, string prefab, int amount)
        {
            string shared = SharedName(prefab);
            Inventory inv = p.GetInventory();
            if (shared == null || inv == null) return false;
            foreach (MethodInfo m in typeof(Inventory).GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                ParameterInfo[] ps = m.GetParameters();
                if (m.Name != "RemoveItem" || ps.Length < 2 || ps[0].ParameterType != typeof(string) || ps[1].ParameterType != typeof(int)) continue;
                int before = inv.CountItems(shared);
                try { m.Invoke(inv, FillArgs(ps, shared, amount)); } catch (Exception ex) { ManagerNPCRP.Instance.Log.LogWarning("[QUEST] RemoveItem failed: " + ex.Message); return false; }
                return before - inv.CountItems(shared) >= amount;
            }
            ManagerNPCRP.Instance.Log.LogError("[QUEST] Inventory.RemoveItem(string,int,...) not found: delivery objectives cannot be consumed.");
            return false;
        }
        /// <summary>Gives items to the local player (PvP coins).</summary>
        internal static void GrantItem(string prefab, int count)
        {
            Player lp = Player.m_localPlayer;
            if (lp == null || count <= 0) return;
            GiveItem(lp, prefab, count);
            lp.Message(MessageHud.MessageType.TopLeft, "+" + count + " × " + (prefab == "Coins" ? "pièces" : prefab) + " (victoire PvP)");
        }
        private static void GiveItem(Player p, string prefab, int count)
        {
            try
            {
                GameObject go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
                if (go == null) { ManagerNPCRP.Instance.Log.LogWarning("[QUEST] reward item '" + prefab + "' unknown: skipped."); return; }
                Inventory inv = p.GetInventory();
                foreach (MethodInfo m in typeof(Inventory).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (m.Name != "AddItem" || ps.Length < 2 || ps[0].ParameterType != typeof(string) || ps[1].ParameterType != typeof(int)) continue;
                    object r = m.Invoke(inv, FillArgs(ps, prefab, count));
                    if (r != null) { p.Message(MessageHud.MessageType.TopLeft, "+ " + count + " × " + prefab); return; }
                    break;
                }
                // inventory full or API mismatch: drop at the player's feet instead of losing the reward
                GameObject drop = UnityEngine.Object.Instantiate(go, p.transform.position + p.transform.forward * 1.2f + Vector3.up * 1.5f, Quaternion.identity);
                ItemDrop idr = drop.GetComponent<ItemDrop>();
                if (idr != null && idr.m_itemData != null) idr.m_itemData.m_stack = count;
                p.Message(MessageHud.MessageType.TopLeft, "Inventaire plein : " + count + " × " + prefab + " déposé à vos pieds.");
            }
            catch (Exception ex) { ManagerNPCRP.Instance.Log.LogWarning("[QUEST] GiveItem failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------ objective tracking
        private static string PrefabName(GameObject go)
        {
            string n = go.name; int i = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return i >= 0 ? n.Substring(0, i).Trim() : n;
        }

        /// <summary>Runs on whichever client owns the dying creature. Only the LAST attacker (a Player) is credited, never the owner by default.</summary>
        internal static void OnDeathPostfix(Character __instance)
        {
            try
            {
                if (__instance == null || Ordered.Count == 0 || __instance is Player || __instance is Viking) return;
                ZNetView nv = __instance.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
                if (nv.GetZDO().GetString(RpIdKey, "").Length > 0) return;      // RP NPC (ally or not): never a hunt target
                if (__instance.IsTamed()) return;
                HitData hit = LastHit != null ? LastHit.GetValue(__instance) as HitData : null;
                Player killer = hit != null ? hit.GetAttacker() as Player : null;
                if (killer == null) return;
                string prefab = PrefabName(__instance.gameObject);
                if (killer == Player.m_localPlayer) CreditKill(prefab);
                else if (ZRoutedRpc.instance != null)
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "RP_QKill", killer.GetPlayerID(), prefab);
            }
            catch (Exception ex) { ManagerNPCRP.Instance?.Log.LogWarning("[QUEST] kill hook failed: " + ex.Message); }
        }
        private static void RpcKill(long sender, long playerId, string prefab)
        {
            Player lp = Player.m_localPlayer;
            if (lp != null && lp.GetPlayerID() == playerId) CreditKill(prefab);   // every other client ignores it
        }
        internal static void GameStartPostfix()
        {
            if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.Register<long, string>("RP_QKill", RpcKill);
                ZRoutedRpc.instance.Register<long, string, string>("RP_QKillNpc", RpcKillNpc);
            }
        }

        private static void CreditKill(string prefab)
        {
            Player lp = Player.m_localPlayer;
            if (lp == null) return;
            foreach (QDef q in Ordered)
            {
                if (q.disabled != null || !IsActive(lp, q.id)) continue;
                Dictionary<string, int> prog = Prog(lp, q.id);
                bool changed = false;
                foreach (QObjective o in q.objectives)
                {
                    if (o.type != "kill" || !o.targets.Any(t => string.Equals(t, prefab, StringComparison.OrdinalIgnoreCase))) continue;
                    int have; prog.TryGetValue(o.id, out have);
                    if (have >= o.count) continue;
                    prog[o.id] = have + 1; changed = true;
                    lp.Message(MessageHud.MessageType.TopLeft, q.name + " : " + o.text + " (" + (have + 1) + "/" + o.count + ")");
                }
                if (changed) { SaveProg(lp, q.id, prog); AnnounceIfReady(lp, q); }
            }
        }
        private static void AnnounceIfReady(Player lp, QDef q)
        {
            if (AllDone(lp, q, Prog(lp, q.id)))
                lp.Message(MessageHud.MessageType.Center, "Objectifs accomplis : " + q.name +
                    (q.journal ? " — à rendre dans le journal (O)" : " — à rendre à " + NpcName(q.turnIn) + " (O)"));
        }

        /// <summary>Delivery objective with a receiving NPC: the items leave the inventory when the player talks to that NPC.</summary>
        private static bool TryHandover(Player lp, QDef q, QObjective o, Dictionary<string, int> prog, string npcId)
        {
            int have; prog.TryGetValue(o.id, out have);
            if (have > 0 || !(string.Equals(o.npc, npcId, StringComparison.OrdinalIgnoreCase) || o.targets.Contains(npcId))) return false;
            int owned = CountItem(lp, o.item);
            if (owned < o.count)
            {
                lp.Message(MessageHud.MessageType.TopLeft, q.name + " : il vous manque " + (o.count - owned) + " × " + o.item + " pour " + NpcName(npcId) + ".");
                return false;
            }
            if (!RemoveItems(lp, o.item, o.count))
            {
                lp.Message(MessageHud.MessageType.TopLeft, "Remise impossible pour le moment.");
                return false;
            }
            prog[o.id] = 1;
            lp.Message(MessageHud.MessageType.TopLeft, q.name + " : " + o.count + " × " + o.item + " remis à " + NpcName(npcId) + " (fait)");
            return true;
        }

        // ---- NPC murders (RP NPC ids, or generic citizens "GEN:<clan>")
        internal static void RelayNpcKill(Player killer, string npcId, string clan)
        {
            if (killer == null) return;
            if (killer == Player.m_localPlayer) CreditNpcKill(npcId, clan);
            else if (ZRoutedRpc.instance != null)
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "RP_QKillNpc", killer.GetPlayerID(), npcId, clan ?? "");
        }
        private static void RpcKillNpc(long sender, long playerId, string npcId, string clan)
        {
            Player lp = Player.m_localPlayer;
            if (lp != null && lp.GetPlayerID() == playerId) CreditNpcKill(npcId, clan);
        }
        private static void CreditNpcKill(string npcId, string clan)
        {
            Player lp = Player.m_localPlayer;
            if (lp == null || string.IsNullOrEmpty(npcId)) return;
            bool generic = npcId.StartsWith("GEN-", StringComparison.Ordinal);
            foreach (QDef q in Ordered)
            {
                if (q.disabled != null || !IsActive(lp, q.id)) continue;
                Dictionary<string, int> prog = Prog(lp, q.id);
                bool changed = false;
                foreach (QObjective o in q.objectives)
                {
                    if (o.type != "killnpc") continue;
                    bool hit = o.targets.Contains(npcId) || (generic && !string.IsNullOrEmpty(clan) && o.targets.Contains("GEN:" + clan));
                    if (!hit) continue;
                    int have; prog.TryGetValue(o.id, out have);
                    if (have >= o.count) continue;
                    prog[o.id] = have + 1; changed = true;
                    lp.Message(MessageHud.MessageType.TopLeft, q.name + " : " + o.text + " (" + (have + 1) + "/" + o.count + ")");
                }
                if (changed) { SaveProg(lp, q.id, prog); AnnounceIfReady(lp, q); }
            }
        }

        /// <summary>Called by the T and O interactions with a given NPC.</summary>
        internal static void OnTalk(string npcId)
        {
            Player lp = Player.m_localPlayer;
            if (lp == null) return;
            foreach (QDef q in Ordered)
            {
                if (q.disabled != null || !IsActive(lp, q.id)) continue;
                Dictionary<string, int> prog = Prog(lp, q.id);
                bool changed = false;
                foreach (QObjective o in q.objectives)
                {
                    int have; prog.TryGetValue(o.id, out have);
                    if (o.type == "deliver" && !string.IsNullOrEmpty(o.npc)) { if (TryHandover(lp, q, o, prog, npcId)) changed = true; continue; }
                    if (o.type == "escort")
                    {
                        if (have == 0 && string.Equals(o.npc, npcId, StringComparison.OrdinalIgnoreCase) && !Escorts.ContainsKey(q.id + "|" + o.id)) StartEscort(lp, q, o);
                        continue;
                    }
                    if (o.type != "talk" || have > 0 || !(string.Equals(o.npc, npcId, StringComparison.OrdinalIgnoreCase) || o.targets.Contains(npcId))) continue;
                    prog[o.id] = 1; changed = true;
                    lp.Message(MessageHud.MessageType.TopLeft, q.name + " : " + o.text + " (fait)");
                }
                if (changed) { SaveProg(lp, q.id, prog); AnnounceIfReady(lp, q); }
            }
        }

        // ------------------------------------------------------------------ parcels and escorts
        /// <summary>Parcels handed over at acceptance are taken back (best effort) when the quest is abandoned or fails.</summary>
        private static void RecallStartItems(Player p, QDef q)
        {
            foreach (KeyValuePair<string, int> si in q.startItems)
            {
                int n = Mathf.Min(si.Value, CountItem(p, si.Key));
                if (n > 0) RemoveItems(p, si.Key, n);
            }
        }

        private sealed class EscortState { public float lastSeen, lastNear; }
        private static readonly Dictionary<string, EscortState> Escorts = new Dictionary<string, EscortState>();   // "quest|objective" -> runtime state
        private const string EscortAtKey = "RP_EscortAt";
        private static float _nextEscort;

        /// <summary>True while a player escorts this NPC (or just finished doing so): the NPC stops wandering.</summary>
        internal static bool IsEscorted(ZDO zdo)
        {
            if (zdo == null || ZNet.instance == null) return false;
            long at = zdo.GetLong(EscortAtKey, 0L);
            return at > 0L && ZNet.instance.GetTimeSeconds() - at < 25.0;
        }

        private static Viking FindNpc(string id)
        {
            foreach (Character c in Character.GetAllCharacters())
            {
                Viking v = c as Viking;
                if (v != null && !v.IsDead() && string.Equals(ReputationManager.IdOf(v), id, StringComparison.OrdinalIgnoreCase)) return v;
            }
            return null;
        }

        /// <summary>Takes (or gives back) control of an escorted NPC. extraSeconds &lt; 0 releases it immediately.</summary>
        private static void HoldNpc(Viking v, Player lp, bool follow, double extraSeconds)
        {
            ZNetView nv = v.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || ZNet.instance == null) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();
            ZDO z = nv.GetZDO();
            if (z == null) return;
            z.Set(EscortAtKey, (long)(ZNet.instance.GetTimeSeconds() + extraSeconds));
            if (!nv.IsOwner()) return;
            try
            {
                if (follow) { if (!v.IsFollowing()) v.Follow(lp.gameObject, lp.GetPlayerName()); }
                else if (v.IsFollowing()) v.UnFollow();                  // Norsemen anchors the NPC where it stands
            }
            catch (Exception)
            {
                MonsterAI ai = v.GetComponent<MonsterAI>();
                if (ai != null) ai.SetFollowTarget(follow ? lp.gameObject : null);
            }
        }

        private static void StartEscort(Player lp, QDef q, QObjective o)
        {
            Viking v = FindNpc(o.npc);
            if (v == null) return;
            Escorts[q.id + "|" + o.id] = new EscortState { lastSeen = Time.time, lastNear = Time.time };
            HoldNpc(v, lp, true, 0.0);
            lp.Message(MessageHud.MessageType.Center, NpcName(o.npc) + " vous suit.");
            lp.Message(MessageHud.MessageType.TopLeft, q.name + " : " + o.text);
        }

        private static void ReleaseEscort(string key, QObjective o, Player lp)
        {
            Viking v = FindNpc(o.npc);
            if (v != null) HoldNpc(v, lp, false, -30.0);
            Escorts.Remove(key);
        }

        private static void FailEscort(Player lp, QDef q, QObjective o, string key, string why)
        {
            ReleaseEscort(key, o, lp);
            lp.m_customData.Remove(StatePrefix + q.id);          // the quest goes back to "available"
            ReadySeen.Remove(q.id);
            RecallStartItems(lp, q);
            lp.Message(MessageHud.MessageType.Center, "Escorte échouée : " + NpcName(o.npc) + " " + why + ".");
        }

        private static void TickEscort()
        {
            Player lp = Player.m_localPlayer;
            if (lp == null || Time.time < _nextEscort || Escorts.Count == 0) return;
            _nextEscort = Time.time + 0.5f;
            foreach (QDef q in new List<QDef>(Ordered))
            {
                bool active = q.disabled == null && IsActive(lp, q.id);
                foreach (QObjective o in q.objectives)
                {
                    if (o.type != "escort") continue;
                    string key = q.id + "|" + o.id;
                    EscortState es;
                    if (!Escorts.TryGetValue(key, out es)) continue;
                    if (!active) { ReleaseEscort(key, o, lp); continue; }
                    Viking v = FindNpc(o.npc);
                    if (v == null)
                    {
                        if (Time.time - es.lastSeen > 20f) FailEscort(lp, q, o, key, "a disparu");
                        continue;
                    }
                    es.lastSeen = Time.time;
                    HoldNpc(v, lp, true, 0.0);
                    float dp = Vector3.Distance(v.transform.position, lp.transform.position);
                    if (dp < 40f) es.lastNear = Time.time;
                    else if (Time.time - es.lastNear > 45f) { FailEscort(lp, q, o, key, "vous a perdu de vue"); continue; }
                    float dx = v.transform.position.x - o.x, dz = v.transform.position.z - o.z;
                    if (dx * dx + dz * dz > o.radius * o.radius || dp > 30f) continue;
                    Dictionary<string, int> prog = Prog(lp, q.id);
                    prog[o.id] = 1;
                    SaveProg(lp, q.id, prog);
                    HoldNpc(v, lp, false, 120.0);                // it stays where it arrived for two minutes, then resumes its life
                    Escorts.Remove(key);
                    lp.Message(MessageHud.MessageType.TopLeft, q.name + " : " + o.text + " (fait)");
                    AnnounceIfReady(lp, q);
                }
            }
        }

        private static void TickVisits()
        {
            Player lp = Player.m_localPlayer;
            if (lp == null || Time.time < _nextTick) return;
            _nextTick = Time.time + 0.5f;
            if (!_prefabsChecked) CheckPrefabs(false);
            Vector3 pos = lp.transform.position;
            foreach (QDef q in Ordered)
            {
                if (q.disabled != null || !IsActive(lp, q.id)) continue;
                Dictionary<string, int> prog = null; bool changed = false;
                foreach (QObjective o in q.objectives)
                {
                    if (o.type != "visit") continue;
                    if (prog == null) prog = Prog(lp, q.id);
                    int have; prog.TryGetValue(o.id, out have);
                    if (have > 0) continue;
                    float dx = pos.x - o.x, dz = pos.z - o.z;
                    if (dx * dx + dz * dz > o.radius * o.radius) continue;
                    prog[o.id] = 1; changed = true;
                    lp.Message(MessageHud.MessageType.TopLeft, q.name + " : " + o.text + " (fait)");
                }
                if (changed) { SaveProg(lp, q.id, prog); AnnounceIfReady(lp, q); }
            }
        }

        // ------------------------------------------------------------------ NPC lookup + T/O entry points
        internal static bool FindNearest(float range, out Viking best, out string bestId)
        {
            best = null; bestId = null;
            Player lp = Player.m_localPlayer; ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (lp == null || plugin == null) return false;
            float bestD = range;
            foreach (Character c in Character.GetAllCharacters())
            {
                Viking v = c as Viking;
                if (v == null || v.IsDead()) continue;
                float d = Vector3.Distance(v.transform.position, lp.transform.position);
                if (d >= bestD) continue;
                string id = ReputationManager.IdOf(v);
                if (id.Length == 0 || !plugin.Npcs.ContainsKey(id)) continue;
                best = v; bestId = id; bestD = d;
            }
            return best != null;
        }

        private static bool GameUiOpen()
        {
            if (NpcTalk.UiBlocked()) return true;
            try
            {
                MethodInfo vis = typeof(InventoryGui).GetMethod("IsVisible", BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (vis != null)
                {
                    object target = null;
                    if (!vis.IsStatic)
                    {
                        Type guiType = typeof(InventoryGui);
                        PropertyInfo instanceProperty = guiType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        FieldInfo instanceField = guiType.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        target = instanceProperty != null ? instanceProperty.GetValue(null, null) :
                                 instanceField != null ? instanceField.GetValue(null) : null;
                    }
                    if ((vis.IsStatic || target != null) && (bool)vis.Invoke(target, null)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Hint shown after a T conversation; never replaces the NPC's own line.</summary>
        internal static void AfterTalk(string npcId)
        {
            Player lp = Player.m_localPlayer;
            if (lp == null || Ordered.Count == 0) return;
            OnTalk(npcId);
            float last;
            if (HintAt.TryGetValue(npcId, out last) && Time.time - last < 15f) return;
            int avail = 0, ready = 0;
            foreach (QDef q in Ordered)
            {
                string r; QuestState s = Evaluate(lp, q, out r);
                if (s == QuestState.Available && q.givers.Contains(npcId)) avail++;
                if (s == QuestState.Ready && q.turnIn == npcId) ready++;
            }
            if (avail + ready == 0) return;
            HintAt[npcId] = Time.time;
            lp.Message(MessageHud.MessageType.TopLeft, "[O] Quêtes : " + avail + " disponible(s), " + ready + " à rendre");
        }

        internal static void Update(bool talkedThisFrame)
        {
            if (Ordered.Count == 0) return;
            TickVisits();
            TickCompletion();
            TickEscort();
            QuestUi.Maintain();
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (Input.GetKeyDown(KeyCode.Escape) && QuestUi.IsOpen) { QuestUi.Close(); return; }
            if (talkedThisFrame || plugin == null || !Input.GetKeyDown(plugin.QuestKeyValue)) return;
            if (QuestUi.IsOpen) { QuestUi.Close(); return; }
            Player lp = Player.m_localPlayer;
            if (lp == null || GameUiOpen()) return;
            Viking v; string id;
            if (!FindNearest(InteractRange, out v, out id)) { QuestUi.Open(null, null); return; }   // no NPC close enough: journal only
            ManagerNPCRP.NpcDefinition npc = plugin.Npcs[id];
            if (ReputationManager.IsRefusing(id, npc.clan)) { NpcTalk.Say(v, ReputationManager.Pick(ReputationManager.RefusalLines(id, npc.clan)), true); return; }
            OnTalk(id);
            QuestUi.Open(v, id);
        }

        // ------------------------------------------------------------------ journal + commands
        internal static void AppendJournal(List<string> lines)
        {
            Player lp = Player.m_localPlayer;
            if (lp == null || Ordered.Count == 0) return;
            int nAct = 0, nAvail = 0, nDone = 0, nSoon = 0;
            List<string> body = new List<string>();
            List<string> chainIds = new List<string>();
            foreach (QDef q in Ordered) if (!chainIds.Contains(q.chain)) chainIds.Add(q.chain);
            string mine = "career_" + ReputationManager.LocalCareer();
            if (ReputationManager.HasChosenCareer() && chainIds.Remove(mine)) chainIds.Insert(0, mine);

            foreach (string cid in chainIds)
            {
                List<string> rows = new List<string>();
                int cDone = 0, cTotal = 0;
                foreach (QDef q in Ordered)
                {
                    if (q.chain != cid || HiddenForPlayer(q)) continue;
                    string r; QuestState s = Evaluate(lp, q, out r);
                    if (s == QuestState.Locked && !ShowLocked(lp, q)) continue;   // no spoilers
                    cTotal++;
                    if (s == QuestState.Completed) { cDone++; nDone++; }
                    else if (s == QuestState.Active || s == QuestState.Ready) nAct++;
                    else if (s == QuestState.Available) nAvail++;
                    else nSoon++;
                    string extra = s == QuestState.Locked ? " — " + r
                                 : s == QuestState.Available ? " — donneur : " + GiverLabel(q)
                                 : s == QuestState.Ready ? " — à rendre : " + TurnInLabel(q) : "";
                    rows.Add("  [" + StatusTag(q, s) + "] " + q.name + " (" + q.id + ")" + extra);
                    if (s == QuestState.Active || s == QuestState.Ready)
                    {
                        Dictionary<string, int> prog = Prog(lp, q.id);
                        foreach (QObjective o in q.objectives)
                        {
                            int h, n; ObjStatus(lp, q, o, prog, out h, out n);
                            rows.Add("      " + (h >= n ? "[x] " : "[ ] ") + o.text + (n > 1 ? " (" + h + "/" + n + ")" : ""));
                        }
                    }
                }
                if (rows.Count == 0) continue;
                QChain ch; Chains.TryGetValue(cid, out ch);
                body.Add((ch != null && ch.title.Length > 0 ? ch.title : cid) + " (" + cDone + "/" + cTotal + " faites) :");
                body.AddRange(rows);
            }

            lines.Add("Quêtes : " + nDone + " faite(s), " + nAct + " en cours, " + nAvail + " disponible(s), " + nSoon + " à venir / verrouillée(s)");
            if (!ReputationManager.HasChosenCareer())
                lines.Add("  (aucune allégeance : les quêtes de votre carrière apparaîtront dès qu'elle vous sera attribuée)");
            if (body.Count == 0) lines.Add("  aucune");
            lines.AddRange(body);
            List<string> fl = new List<string>();
            foreach (QDef q in Ordered) if (IsCompleted(lp, q.id)) foreach (string f in q.rewardFlags) fl.Add(f);
            if (fl.Count > 0) lines.Add("Événements débloqués : " + string.Join(", ", fl.ToArray()));
        }

        internal static void RegisterCommands()
        {
            new NorseCommand("rp_quests", "List your quests (active, available, completed).",
                args => { List<string> l = new List<string>(); AppendJournal(l); foreach (string s in l) { if (Console.instance != null) Console.instance.Print(s); else ManagerNPCRP.Instance.Log.LogInfo(s); } return true; },
                adminOnly: false);
            new NorseCommand("rp_quest_check", "Validate quest definitions against the loaded game (prefabs, items).",
                args => { string r = CheckPrefabs(true); if (Console.instance != null) Console.instance.Print("[QUEST] " + r); return true; },
                adminOnly: true);
            new NorseCommand("rp_quest", "Quest admin: rp_quest reset <QuestId|all> | rp_quest state <QuestId>",
                args =>
                {
                    string[] a = ManagerNPCRP.CmdArgs(args.Args, "rp_quest");
                    Player lp = Player.m_localPlayer; if (lp == null || a.Length < 3) { ManagerNPCRP.Instance.Log.LogInfo("Usage: rp_quest reset <id|all> | rp_quest state <id>"); return true; }
                    if (a[1] == "reset")
                    {
                        foreach (QDef q in Ordered) if (a[2] == "all" || string.Equals(a[2], q.id, StringComparison.OrdinalIgnoreCase)) lp.m_customData.Remove(StatePrefix + q.id);
                        lp.Message(MessageHud.MessageType.TopLeft, "Quest state reset: " + a[2]);
                    }
                    else if (a[1] == "state") { string r; QDef q; if (Quests.TryGetValue(a[2], out q)) lp.Message(MessageHud.MessageType.TopLeft, q.id + " = " + Evaluate(lp, q, out r) + " " + r + " raw=" + Raw(lp, q.id)); }
                    return true;
                }, adminOnly: true);
        }
        private static void LogDbg(string m)
        {
            if (Db != null && Db.settings != null && Db.settings.debug) ManagerNPCRP.Instance.Log.LogInfo("[QUEST] " + m);
        }
    }

    // =====================================================================
    // QUEST UI  (IMGUI window: the mod has no uGUI asset bundle, IMGUI is the compatible option)
    // =====================================================================
    internal static class QuestUi
    {
        private static bool _open;
        private static Viking _viking;
        private static string _npcId;
        private static int _tab;                 // 0 = this NPC, 1 = journal, 2 = reputation
        private static string _sel;
        private static string _confirmAbandon;
        private static string _msg = "";
        private static Vector2 _scrollL, _scrollR;
        private static Rect _rect = new Rect(0, 0, 1100, 700);
        private static int _filter;              // journal filter: 0 all, 1 in progress, 2 available, 3 done
        private static Vector2 _scrollRep;
        private static bool _placed;
        private static float _uiScale = 1f;
        private static int _lastScreenWidth, _lastScreenHeight;
        private static GUISkin _questSkin;
        private static Texture2D _panelTexture, _buttonTexture, _buttonHoverTexture, _buttonActiveTexture;
        private static GUIStyle _tabStyle, _accentLabelStyle;
        internal static bool IsOpen { get { return _open; } }

        private static Texture2D SolidTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private static Texture2D FramedTexture(int width, int height, Color fill, Color edge, Color accent)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int d = Mathf.Min(x, y, width - 1 - x, height - 1 - y);
                    Color c = d == 0 ? edge : d <= 2 ? accent : fill;
                    if (d > 2)
                    {
                        float grain = (((x * 17 + y * 31) % 13) - 6) * 0.0015f;
                        c = new Color(Mathf.Clamp01(c.r + grain), Mathf.Clamp01(c.g + grain), Mathf.Clamp01(c.b + grain), 1f);
                    }
                    pixels[y * width + x] = c;
                }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static void EnsureQuestSkin()
        {
            if (_questSkin != null) return;
            _questSkin = UnityEngine.Object.Instantiate(GUI.skin);
            _questSkin.hideFlags = HideFlags.HideAndDontSave;
            _panelTexture = FramedTexture(128, 128, new Color(0.095f, 0.072f, 0.045f), new Color(0.035f, 0.026f, 0.018f), new Color(0.31f, 0.22f, 0.105f));
            _buttonTexture = FramedTexture(64, 48, new Color(0.19f, 0.135f, 0.075f), new Color(0.055f, 0.04f, 0.025f), new Color(0.34f, 0.235f, 0.105f));
            _buttonHoverTexture = FramedTexture(64, 48, new Color(0.27f, 0.19f, 0.10f), new Color(0.07f, 0.05f, 0.028f), new Color(0.49f, 0.34f, 0.14f));
            _buttonActiveTexture = FramedTexture(64, 48, new Color(0.35f, 0.24f, 0.105f), new Color(0.075f, 0.052f, 0.025f), new Color(0.67f, 0.45f, 0.17f));

            _questSkin.window = new GUIStyle(_questSkin.window);
            _questSkin.window.normal.background = _panelTexture;
            _questSkin.window.hover.background = _panelTexture;
            _questSkin.window.active.background = _panelTexture;
            _questSkin.window.focused.background = _panelTexture;
            _questSkin.window.onNormal.background = _panelTexture;
            _questSkin.window.onHover.background = _panelTexture;
            _questSkin.window.onActive.background = _panelTexture;
            _questSkin.window.onFocused.background = _panelTexture;
            _questSkin.window.padding = new RectOffset(14, 14, 12, 14);
            _questSkin.window.border = new RectOffset(6, 6, 6, 6);
            _questSkin.window.normal.textColor = new Color(0.91f, 0.76f, 0.48f);
            _questSkin.window.fontSize = 15;
            _questSkin.window.fontStyle = FontStyle.Bold;

            _questSkin.button = WarmButtonStyle(_questSkin.button);
            _tabStyle = WarmButtonStyle(_questSkin.button);
            _questSkin.toggle = WarmButtonStyle(_questSkin.toggle);
            _questSkin.toggle.onNormal.background = _buttonActiveTexture;
            _questSkin.toggle.onNormal.textColor = new Color(1f, 0.91f, 0.68f);
            _questSkin.toggle.onHover.background = _buttonActiveTexture;
            _questSkin.toggle.onHover.textColor = Color.white;
            _questSkin.label = new GUIStyle(_questSkin.label);
            _questSkin.label.richText = true;
            _questSkin.label.normal.textColor = new Color(0.84f, 0.81f, 0.73f);
            _questSkin.label.fontSize = 14;
            _questSkin.label.wordWrap = true;
            _accentLabelStyle = new GUIStyle(_questSkin.label);
            _accentLabelStyle.normal.textColor = new Color(0.91f, 0.76f, 0.48f);
            _accentLabelStyle.hover.textColor = _accentLabelStyle.normal.textColor;
            _accentLabelStyle.active.textColor = _accentLabelStyle.normal.textColor;
            _accentLabelStyle.focused.textColor = _accentLabelStyle.normal.textColor;
            _accentLabelStyle.fontSize = 15;
            _accentLabelStyle.fontStyle = FontStyle.Bold;
            _accentLabelStyle.wordWrap = true;
            _questSkin.box = new GUIStyle(_questSkin.box);
            _questSkin.box.normal.background = _panelTexture;
            _questSkin.box.hover.background = _panelTexture;
            _questSkin.box.active.background = _panelTexture;
            _questSkin.box.focused.background = _panelTexture;
            _questSkin.box.border = new RectOffset(6, 6, 6, 6);
            _questSkin.scrollView = new GUIStyle(_questSkin.scrollView);
            _questSkin.scrollView.normal.background = _panelTexture;
            _questSkin.scrollView.border = new RectOffset(6, 6, 6, 6);
        }

        private static GUIStyle WarmButtonStyle(GUIStyle source)
        {
            GUIStyle style = new GUIStyle(source);
            style.normal.background = _buttonTexture;
            style.hover.background = _buttonHoverTexture;
            style.active.background = _buttonActiveTexture;
            style.focused.background = _buttonHoverTexture;
            style.onNormal.background = _buttonActiveTexture;
            style.onHover.background = _buttonActiveTexture;
            style.onActive.background = _buttonActiveTexture;
            style.onFocused.background = _buttonActiveTexture;
            style.normal.textColor = new Color(0.88f, 0.82f, 0.69f);
            style.hover.textColor = new Color(1f, 0.91f, 0.68f);
            style.active.textColor = Color.white;
            style.onNormal.textColor = new Color(1f, 0.91f, 0.68f);
            style.onHover.textColor = Color.white;
            style.onActive.textColor = Color.white;
            style.onFocused.textColor = Color.white;
            style.fontSize = 14;
            style.padding = new RectOffset(12, 12, 7, 7);
            style.margin = new RectOffset(3, 3, 3, 3);
            style.border = new RectOffset(5, 5, 5, 5);
            style.wordWrap = true;
            return style;
        }

        private static void UpdateScaleAndPosition(bool center)
        {
            // The IMGUI layout is authored for a 1920x1080 screen. Keep scaling
            // with larger displays, but leave a small margin so the window stays
            // fully on screen (including on ultrawide and very high resolutions).
            float resolutionScale = Mathf.Min(Screen.width / 1920f, Screen.height / 1080f);
            float fitScale = Mathf.Min(Screen.width * 0.9f / _rect.width, Screen.height * 0.9f / _rect.height);
            _uiScale = Mathf.Max(0.5f, Mathf.Min(resolutionScale, fitScale));
            if (center || Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight)
            {
                _rect.x = (Screen.width / _uiScale - _rect.width) / 2f;
                _rect.y = (Screen.height / _uiScale - _rect.height) / 2f;
                _lastScreenWidth = Screen.width;
                _lastScreenHeight = Screen.height;
            }
        }

        internal static void Open(Viking v, string npcId)
        {
            _viking = v; _npcId = npcId; _tab = npcId != null ? 0 : 1; _filter = 0; _sel = null; _msg = ""; _confirmAbandon = null; _open = true;
            if (ReputationManager.HasChosenCareer()) ReputationManager.RequestSnapshot(true);
            UpdateScaleAndPosition(true); _placed = true;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            if (v != null) SpeakState();
        }
        internal static void Close()
        {
            if (!_open) return;
            _open = false; _viking = null; _npcId = null;
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }
        /// <summary>Drops stale interactions: NPC dead, destroyed, or the player walked away.</summary>
        internal static void Maintain()
        {
            if (!_open || _npcId == null) return;
            Player lp = Player.m_localPlayer;
            if (lp == null) { Close(); return; }
            bool gone = _viking == null || _viking.IsDead() ||
                        Vector3.Distance(_viking.transform.position, lp.transform.position) > QuestEngine.InteractRange + 2f;
            if (gone) { _npcId = null; _viking = null; if (_tab == 0) _tab = 1; _sel = null; _msg = "Vous vous êtes éloigné : seul le journal est consultable."; }
        }
        private static void SpeakState()
        {
            Player lp = Player.m_localPlayer; string r;
            QDef pick = null; QuestState ps = QuestState.Locked;
            foreach (QDef q in QuestEngine.Ordered)
            {
                QuestState s = QuestEngine.Evaluate(lp, q, out r);
                if (s == QuestState.Ready && q.turnIn == _npcId) { pick = q; ps = s; break; }
                if (s == QuestState.Available && q.givers.Contains(_npcId) && (pick == null || ps != QuestState.Active)) { pick = q; ps = s; }
                if (s == QuestState.Active && (q.givers.Contains(_npcId) || q.turnIn == _npcId) && pick == null) { pick = q; ps = s; }
            }
            if (pick != null) NpcTalk.Say(_viking, QuestEngine.Line(pick, ps), true);
        }

        internal static void OnGui()
        {
            if (!_open || !_placed) return;
            GUI.depth = -1000;
            UpdateScaleAndPosition(false);
            EnsureQuestSkin();
            GUISkin oldSkin = GUI.skin;
            Color oldColor = GUI.color;
            Color oldContentColor = GUI.contentColor;
            Color oldBackgroundColor = GUI.backgroundColor;
            Matrix4x4 oldMatrix = GUI.matrix;
            try
            {
                GUI.skin = _questSkin;
                // Keep the game palette from tinting the quest panel and its text.
                GUI.color = Color.white;
                GUI.contentColor = Color.white;
                GUI.backgroundColor = Color.white;
                GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_uiScale, _uiScale, 1f)) * oldMatrix;
                _rect = GUI.Window(0x5251, _rect, DrawWindow, string.Empty, _questSkin.window);
            }
            finally
            {
                GUI.matrix = oldMatrix;
                GUI.skin = oldSkin;
                GUI.color = oldColor;
                GUI.contentColor = oldContentColor;
                GUI.backgroundColor = oldBackgroundColor;
            }
        }

        // Reserve room for the title/navigation rows, then let the scroll panes
        // occupy the remaining window height instead of leaving a blank strip.
        private static float PaneH { get { return Mathf.Max(260f, _rect.height - 160f); } }

        private static string CareerHeader()
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            if (plugin == null || plugin.Database == null) return "";
            if (!ReputationManager.HasChosenCareer()) return "Allégeance : aucune";
            string career = ReputationManager.LocalCareer(); CareerDefinition cd;
            return "Allégeance : " + (plugin.Database.careers.TryGetValue(career, out cd) && cd != null && !string.IsNullOrEmpty(cd.name) ? cd.name : career);
        }

        private static void DrawWindow(int id)
        {
            Player lp = Player.m_localPlayer;
            if (lp == null) { Close(); return; }
            GUILayout.BeginHorizontal(GUI.skin.box);
            GUILayout.Label("<color=#e2b763><b>JOURNAL DES QUÊTES</b></color>", GUILayout.Width(220), GUILayout.Height(38));
            if (_npcId != null && GUILayout.Toggle(_tab == 0, "Ce PNJ", _tabStyle ?? GUI.skin.button, GUILayout.MinWidth(125), GUILayout.Height(38))) _tab = 0;
            if (GUILayout.Toggle(_tab == 1, "Journal", _tabStyle ?? GUI.skin.button, GUILayout.MinWidth(125), GUILayout.Height(38))) _tab = 1;
            if (GUILayout.Toggle(_tab == 2, "Réputation", _tabStyle ?? GUI.skin.button, GUILayout.MinWidth(145), GUILayout.Height(38))) { if (_tab != 2) ReputationManager.RequestSnapshot(true); _tab = 2; }
            if (GUILayout.Toggle(_tab == 3, "Faction", _tabStyle ?? GUI.skin.button, GUILayout.MinWidth(125), GUILayout.Height(38))) { if (_tab != 3) ReputationManager.RequestRoster(true); _tab = 3; }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Fermer  ×", GUILayout.Width(110), GUILayout.Height(38))) { Close(); return; }
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            GUILayout.BeginHorizontal(GUI.skin.box);
            GUILayout.Label("<color=#e8c779>" + CareerHeader() + "</color>", _accentLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label(_tab == 0 ? "Quêtes de ce personnage" : _tab == 1 ? "Votre journal d’aventure" : _tab == 2 ? "Réputation et progression" : "Votre faction et compagnie");
            GUILayout.EndHorizontal();
            if (_msg.Length > 0) GUILayout.Label("<color=#ffd27f>" + _msg + "</color>", GUI.skin.box);

            if (_tab == 2) DrawReputation(lp);
            else if (_tab == 3) DrawFaction(lp);
            else DrawQuestPane(lp);

            // The navigation buttons occupy the top of the window. A full-width
            // drag region here overlaps them and can swallow their clicks.
        }

        // ------------------------------------------------------------------ quests tab (this NPC / journal)
        private static List<string> ChainOrder()
        {
            List<string> l = new List<string>();
            string mine = "career_" + ReputationManager.LocalCareer();
            if (ReputationManager.HasChosenCareer() && QuestEngine.Chains.ContainsKey(mine)) l.Add(mine);
            foreach (QDef q in QuestEngine.Ordered) if (!l.Contains(q.chain)) l.Add(q.chain);
            return l;
        }

        private static void DrawChainHeader(Player lp, string cid)
        {
            QChain ch; QuestEngine.Chains.TryGetValue(cid, out ch);
            int done = 0, total = 0;
            foreach (QDef q in QuestEngine.Ordered)
            {
                if (q.chain != cid || QuestEngine.HiddenForPlayer(q)) continue;
                total++;
                if (QuestEngine.IsCompleted(lp, q.id)) done++;
            }
            GUILayout.Label("<b>" + (ch != null && ch.title.Length > 0 ? ch.title : cid) + "</b>  <color=#99aaaa>(" + done + "/" + total + " faites)</color>");
        }

        private static void DrawQuestPane(Player lp)
        {
            bool npcTab = _tab == 0 && _npcId != null;
            int nAct = 0, nAvail = 0, nDone = 0, nAll = 0;
            List<QDef> rows = new List<QDef>();
            Dictionary<string, QuestState> states = new Dictionary<string, QuestState>(StringComparer.OrdinalIgnoreCase);
            foreach (QDef q in QuestEngine.Ordered)
            {
                if (QuestEngine.HiddenForPlayer(q)) continue;
                string reason; QuestState s = QuestEngine.Evaluate(lp, q, out reason);
                states[q.id] = s;
                if (npcTab)
                {
                    bool mine = q.givers.Contains(_npcId) || q.turnIn == _npcId;
                    if (mine && Visible(lp, q, s)) rows.Add(q);
                    continue;
                }
                bool listed = s != QuestState.Locked || Visible(lp, q, s);
                if (s == QuestState.Active || s == QuestState.Ready) nAct++;
                else if (s == QuestState.Available) nAvail++;
                else if (s == QuestState.Completed) nDone++;
                if (listed) nAll++;
                bool pass = _filter == 0 ? listed
                          : _filter == 1 ? (s == QuestState.Active || s == QuestState.Ready)
                          : _filter == 2 ? s == QuestState.Available
                          : s == QuestState.Completed;
                if (pass) rows.Add(q);
            }

            if (!npcTab && ReputationManager.HasChosenCareer() && QuestEngine.CampaignDone(lp, ReputationManager.LocalCareer()))
                GUILayout.Label("<color=#ffd27f><b>Campagne de carrière accomplie</b></color> — tous les mois ont été rendus.");
            if (!npcTab)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);
                GUILayout.Label("Afficher", GUILayout.Width(68));
                if (GUILayout.Toggle(_filter == 0, "Toutes  " + nAll, _tabStyle ?? GUI.skin.button, GUILayout.Height(34))) _filter = 0;
                if (GUILayout.Toggle(_filter == 1, "En cours  " + nAct, _tabStyle ?? GUI.skin.button, GUILayout.Height(34))) _filter = 1;
                if (GUILayout.Toggle(_filter == 2, "Disponibles  " + nAvail, _tabStyle ?? GUI.skin.button, GUILayout.Height(34))) _filter = 2;
                if (GUILayout.Toggle(_filter == 3, "Terminées  " + nDone, _tabStyle ?? GUI.skin.button, GUILayout.Height(34))) _filter = 3;
                GUILayout.EndHorizontal();
            }
            float paneH = npcTab ? PaneH : PaneH - 30f;

            GUILayout.BeginHorizontal(GUI.skin.box);
            // ---- left: quest list grouped by chain
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(380));
            GUILayout.Label("<b>Quêtes</b>  <color=#b49b70>— sélectionnez une entrée</color>");
            _scrollL = GUILayout.BeginScrollView(_scrollL, GUILayout.Height(paneH));
            if (rows.Count == 0) GUILayout.Label(npcTab ? "Ce PNJ n'a aucune quête pour vous." : "Aucune quête dans cette catégorie.");
            if (!npcTab && !ReputationManager.HasChosenCareer())
                GUILayout.Label("<color=#99aaaa>Les quêtes de votre carrière apparaîtront ici dès qu'une allégeance vous sera attribuée.</color>");
            foreach (string cid in ChainOrder())
            {
                bool header = false;
                foreach (QDef q in rows)
                {
                    if (q.chain != cid) continue;
                    if (!header) { header = true; DrawChainHeader(lp, cid); }
                    QuestState s = states[q.id];
                    Color old = GUI.contentColor;
                    GUI.contentColor = QuestEngine.StatusColor(s);
                    bool on = GUILayout.Toggle(_sel == q.id, "[" + QuestEngine.StatusTag(q, s) + "]  " + q.name, GUI.skin.button, GUILayout.MinHeight(42));
                    GUI.contentColor = old;
                    if (on && _sel != q.id) { _sel = q.id; _confirmAbandon = null; _msg = ""; }
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            // ---- right: detail
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b>Détails et actions</b>");
            _scrollR = GUILayout.BeginScrollView(_scrollR, GUILayout.Height(paneH));
            QDef sel; if (_sel != null && QuestEngine.Quests.TryGetValue(_sel, out sel)) DrawDetail(lp, sel); else GUILayout.Label("Sélectionnez une quête.");
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        /// <summary>Hide locked quests that only wait for an earlier quest of the chain (no spoilers); show the others with their reason.</summary>
        private static bool Visible(Player lp, QDef q, QuestState s)
        {
            return s != QuestState.Locked || QuestEngine.ShowLocked(lp, q);
        }

        private static void DrawDetail(Player lp, QDef q)
        {
            string reason; QuestState s = QuestEngine.Evaluate(lp, q, out reason);
            QChain ch; QuestEngine.Chains.TryGetValue(q.chain, out ch);
            string career = ReputationManager.LocalCareer();
            GUILayout.Label("<b>" + q.name + "</b>");
            Color old = GUI.contentColor;
            GUI.contentColor = QuestEngine.StatusColor(s);
            GUILayout.Label("Statut : " + QuestEngine.StatusTag(q, s) + (s == QuestState.Locked && reason.Length > 0 ? " — " + reason : ""));
            GUI.contentColor = old;
            GUILayout.Label((ch != null ? ch.title + " — " : "") + (q.clan.Length > 0 ? q.clan + " " : "") + "(" + q.faction + ")" +
                            (q.biome.Length > 0 ? " — " + q.biome : "") + (q.career ? " — mois " + q.tier : q.tier > 0 ? " — palier " + q.tier : " — quête secondaire (hors canon)"));
            if (q.description.Length > 0) GUILayout.Label(q.description);
            string line = QuestEngine.Line(q, s);
            if (line.Length > 0) GUILayout.Label("<i>« " + line + " »</i>");

            // ---- objectives + overall progress
            bool tracked = s == QuestState.Active || s == QuestState.Ready || s == QuestState.Completed;
            Dictionary<string, int> prog = ProgOf(lp, q);
            int doneObj = 0;
            foreach (QObjective o in q.objectives)
            {
                int h, n; QuestEngine.ObjStatus(lp, q, o, prog, out h, out n);
                if (s == QuestState.Completed || (tracked && h >= n)) doneObj++;
            }
            GUILayout.Space(6);
            if (tracked)
            {
                GUILayout.Label("<b>Objectifs</b>  (" + doneObj + "/" + q.objectives.Count + ")");
                Bar(q.objectives.Count > 0 ? doneObj / (float)q.objectives.Count : 0f, QuestEngine.StatusColor(s), 300f, 10f);
            }
            else GUILayout.Label("<b>Objectifs</b>");
            foreach (QObjective o in q.objectives)
            {
                int h = 0, n = 1;
                if (tracked)
                {
                    if (s == QuestState.Completed) { h = 1; n = 1; }
                    else QuestEngine.ObjStatus(lp, q, o, prog, out h, out n);
                }
                bool okObj = h >= n && tracked;
                string objLine = (okObj ? "[x] " : "[ ] ") + o.text + (n > 1 && s != QuestState.Completed ? " (" + h + "/" + n + ")" : "") +
                                 (o.type == "deliver" ? (string.IsNullOrEmpty(o.npc) ? "  — retiré de l'inventaire à la remise de la quête" : "  — en votre possession : " + QuestEngine.OwnedCount(lp, o.item) + "/" + o.count + " (retiré de l'inventaire à la remise)") : "");
                GUILayout.Label(okObj ? "<color=#8fe08f>" + objLine + "</color>" : objLine);
            }
            GUILayout.Space(6);
            GUILayout.Label("Donneur : " + QuestEngine.GiverLabel(q) + "   —   Remise : " + QuestEngine.TurnInLabel(q));

            // ---- rewards
            string gain = QuestEngine.RepGainText(q, career), loss = QuestEngine.RepLossText(q, career);
            string rw = gain.Length > 0 ? "réputation " + gain : (q.repClanPoints > 0 ? "réputation " + q.clan + " +" + q.repClanPoints : "");
            foreach (KeyValuePair<string, int> it in q.rewardItems)
                rw += (rw.Length > 0 ? ", " : "") + it.Value + " × " + (it.Key == "Coins" ? "pièces" : it.Key);
            GUILayout.Label("Récompenses : " + (rw.Length > 0 ? rw : "—"));
            if (loss.Length > 0) GUILayout.Label("<color=#ff8f7f>Réputation perdue : " + loss + "</color>");
            if (q.career && (gain.Length > 0 || loss.Length > 0))
                GUILayout.Label("<color=#99aaaa><i>Réputation et pièces sont versées à la remise de la quête.</i></color>");
            GUILayout.Space(8);

            // ---- actions
            GUILayout.BeginHorizontal(GUI.skin.box);
            bool atGiver = _npcId != null && q.givers.Contains(_npcId);
            if (s == QuestState.Available && (q.journal || atGiver))
            {
                if (GUILayout.Button(q.journal ? "Accepter la quête" : "Accepter la quête (PNJ)", GUILayout.MinHeight(40)))
                {
                    _msg = QuestEngine.Accept(lp, q, _npcId);
                    if (_msg.Length == 0)
                    {
                        if (!q.journal && _viking != null) NpcTalk.Say(_viking, q.dlg["accepted"], true);
                        lp.Message(MessageHud.MessageType.TopLeft, "Quête acceptée : " + q.name);
                    }
                }
            }
            else if (s == QuestState.Available) GUILayout.Label("À accepter auprès de " + QuestEngine.GiverLabel(q) + ".");

            if (s == QuestState.Ready && (q.journal || _npcId == q.turnIn))
            {
                if (GUILayout.Button("Rendre la quête", GUILayout.MinHeight(40)))
                {
                    string done; _msg = QuestEngine.TurnIn(lp, q, _npcId, out done);
                    if (_msg.Length == 0 && !q.journal && _viking != null) NpcTalk.Say(_viking, done, true);
                }
            }
            else if (s == QuestState.Ready) GUILayout.Label("À rendre auprès de " + QuestEngine.NpcName(q.turnIn) + ".");

            if ((s == QuestState.Active || s == QuestState.Ready) && GUILayout.Button(_confirmAbandon == q.id ? "Confirmer l'abandon" : "Abandonner", GUILayout.MinHeight(40)))
            {
                if (_confirmAbandon == q.id) { _msg = QuestEngine.Abandon(lp, q); _confirmAbandon = null; } else _confirmAbandon = q.id;
            }
            GUILayout.EndHorizontal();
        }

        private static Dictionary<string, int> ProgOf(Player lp, QDef q)
        {
            Dictionary<string, int> d = new Dictionary<string, int>();
            string v;
            if (!lp.m_customData.TryGetValue("RP_QS_" + q.id, out v) || !v.StartsWith("A;", StringComparison.Ordinal)) return d;
            foreach (string part in v.Substring(2).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('='); int n;
                if (eq > 0 && int.TryParse(part.Substring(eq + 1), out n)) d[part.Substring(0, eq)] = n;
            }
            return d;
        }

        // ------------------------------------------------------------------ reputation tab
        private static void Bar(float frac, Color fill, float w, float h, params float[] ticks)
        {
            Rect rc = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(rc, Texture2D.whiteTexture);
            GUI.color = fill;
            GUI.DrawTexture(new Rect(rc.x, rc.y, rc.width * Mathf.Clamp01(frac), rc.height), Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            foreach (float t in ticks) GUI.DrawTexture(new Rect(rc.x + rc.width * Mathf.Clamp01(t) - 0.5f, rc.y, 1f, rc.height), Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static string FactionLabel(string f)
        {
            switch (f)
            {
                case "DouzeCouronnes": return "Douze Couronnes";
                case "CamaradesAzures": return "Camarades Azurés";
                case "HordeSolaire": return "Horde Solaire";
                default: return f;
            }
        }

        private static Color TierColor(int t)
        {
            switch (t)
            {
                case 1: return new Color(0.80f, 0.72f, 0.35f);
                case 2: return new Color(0.50f, 0.78f, 0.50f);
                case 3: return new Color(0.42f, 0.72f, 1f);
                default: return new Color(0.62f, 0.62f, 0.62f);
            }
        }

        private static string FactionOfClan(string clan, ManagerNPCRP.ClanDefinition cd)
        {
            if (clan == "Itinerants") return "Vagabonds";
            return cd != null && cd.faction != null ? cd.faction : "";
        }

        private static void DrawReputation(Player lp)
        {
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            ManagerNPCRP.NpcDatabase db = plugin != null ? plugin.Database : null;
            if (db == null) { GUILayout.Label("Base de données indisponible."); return; }
            ReputationSettings r = db.reputation;
            string career = ReputationManager.LocalCareer();
            ReputationManager.RequestSnapshot(false);
            RepSnapshot snap = ReputationManager.Snapshot;
            if (snap != null && !string.Equals(snap.career, career, StringComparison.OrdinalIgnoreCase)) snap = null;

            GUILayout.BeginHorizontal(GUI.skin.box);
            GUILayout.Label(ReputationManager.HasChosenCareer()
                ? "Votre réputation par faction et par clan — " + CareerHeader()
                : "<color=#ffbd91>Aucune allégeance choisie : réputation de la carrière par défaut (" + career + ").</color>", _accentLabelStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(" Actualiser ")) ReputationManager.RequestSnapshot(true);
            GUILayout.EndHorizontal();
            if (snap == null)
                GUILayout.Label("<color=#99aaaa>Points exacts indisponibles pour le moment : seuls les paliers sont affichés.</color>");

            bool bars = ReputationManager.HasChosenCareer();
            if (bars) DrawCareerBars(lp, db, career, snap);
            _scrollRep = GUILayout.BeginScrollView(_scrollRep, GUILayout.Height(PaneH - (bars ? 90f : 10f)));
            GUILayout.Label("<b>Factions</b>");
            foreach (string f in r.repFactions) DrawRepRow(FactionLabel(f), true, f, snap, r, lp, career);
            GUILayout.Space(8);
            GUILayout.Label("<b>Clans</b>");
            foreach (string f in r.repFactions)
            {
                List<string> clans = new List<string>();
                foreach (KeyValuePair<string, ManagerNPCRP.ClanDefinition> kv in db.clans)
                    if (FactionOfClan(kv.Key, kv.Value) == f) clans.Add(kv.Key);
                if (clans.Count == 0) continue;
                GUILayout.Label("<color=#c9b458>— " + FactionLabel(f) + " —</color>");
                foreach (string c in clans) DrawRepRow(c, false, c, snap, r, lp, career);
            }
            GUILayout.EndScrollView();
        }

        private static Vector2 _scrollFac;

        /// <summary>"Faction" tab: a recap of my faction (members, who is online, companies, PvP score).</summary>
        private static void DrawFaction(Player lp)
        {
            ReputationManager.RequestRoster(false);
            RosterSnap r = ReputationManager.Roster;
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            CareerDefinition cd = null;
            if (r != null && plugin != null && plugin.Database != null && r.career.Length > 0) plugin.Database.careers.TryGetValue(r.career, out cd);

            GUILayout.BeginHorizontal();
            if (r != null && r.career.Length > 0)
            {
                int online = 0, kills = 0, deaths = 0;
                foreach (RosterMember m in r.members) { if (m.online) online++; kills += m.kills; deaths += m.deaths; }
                int comps = 0;
                foreach (CompanyInfo ci in r.companies) if (string.Equals(ci.career, r.career, StringComparison.OrdinalIgnoreCase)) comps++;
                GUILayout.Label("<b>" + (cd != null ? FactionLabel(cd.faction) : r.career) + "</b>   —   " + r.members.Count + " membre(s), " + online +
                                " en ligne   —   " + comps + " compagnie(s)   —   PvP de la faction : " + kills + " victoire(s) / " + deaths + " défaite(s)", _accentLabelStyle);
            }
            else GUILayout.Label("<b>Faction</b>");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(" Actualiser ")) ReputationManager.RequestRoster(true);
            GUILayout.EndHorizontal();

            _scrollFac = GUILayout.BeginScrollView(_scrollFac, GUILayout.Height(PaneH - 10f));
            DrawRoster(lp);
            GUILayout.EndScrollView();
        }

        private static string _coName = "", _coTarget = "", _pendingSwitch = "";

        /// <summary>My faction's players, my company, the invitations and the companies everybody knows.</summary>
        private static string CareerLabelOf(ManagerNPCRP.NpcDatabase db, string key)
        {
            CareerDefinition cd;
            return db != null && !string.IsNullOrEmpty(key) && db.careers.TryGetValue(key, out cd) && cd != null ? FactionLabel(cd.faction) : (string.IsNullOrEmpty(key) ? "aucune" : key);
        }

        /// <summary>Allegiance choice/switch, my company (member / chief tools), my faction's players, companies known to everybody.</summary>
        private static void DrawRoster(Player lp)
        {
            ReputationManager.RequestRoster(false);
            RosterSnap r = ReputationManager.Roster;
            ManagerNPCRP plugin = ManagerNPCRP.Instance;
            ManagerNPCRP.NpcDatabase db = plugin != null ? plugin.Database : null;
            string me = lp.GetPlayerName();
            string mine = ReputationManager.HasChosenCareer() ? ReputationManager.LocalCareer() : "";

            GUILayout.Label("<b>Allégeance</b>");
            GUILayout.Label(mine.Length > 0 ? "Actuelle : <b>" + CareerLabelOf(db, mine) + "</b>"
                : "<color=#ff9f7f>Aucune allégeance.</color> Choisissez-en une, ou rejoignez une compagnie affectée à une faction : vous hériterez de la sienne.");
            if (db != null)
            {
                GUILayout.BeginHorizontal();
                foreach (KeyValuePair<string, CareerDefinition> kv in db.careers)
                {
                    bool cur = string.Equals(kv.Key, mine, StringComparison.OrdinalIgnoreCase);
                    GUI.enabled = !cur;
                    if (GUILayout.Button(cur ? FactionLabel(kv.Value.faction) + " (actuelle)" : FactionLabel(kv.Value.faction), GUILayout.Width(160))) _pendingSwitch = kv.Key;
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
                if (_pendingSwitch.Length > 0 && !string.Equals(_pendingSwitch, mine, StringComparison.OrdinalIgnoreCase))
                {
                    GUILayout.Label("<color=#ffd27f>" + (mine.Length > 0
                        ? "Passer chez : " + CareerLabelOf(db, _pendingSwitch) + ". Vous perdez la réputation de " + CareerLabelOf(db, mine) + " (elle appartient à la carrière), vos quêtes de carrière en cours sont abandonnées (les quêtes rendues restent acquises) et vous quittez votre compagnie si son allégeance diffère."
                        : "Choisir : " + CareerLabelOf(db, _pendingSwitch) + ".") + "</color>");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(" Confirmer ", GUILayout.Width(120))) { ReputationManager.SwitchAllegiance(_pendingSwitch); _pendingSwitch = ""; }
                    if (GUILayout.Button(" Annuler ", GUILayout.Width(120))) _pendingSwitch = "";
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.Space(4);
            GUILayout.Label("<color=#99aaaa>PvP : vaincre un joueur d'une autre allégeance rapporte 1 point de réputation à votre faction et 10 pièces ; être vaincu par une faction étrangère coûte 1 point. Aucun dégât entre membres de la même faction.</color>");
            if (r == null) { GUILayout.Label("<color=#99aaaa>Liste des compagnies indisponible pour le moment.</color>"); return; }

            CompanyInfo myCo = null;
            foreach (CompanyInfo ci in r.companies) if (ci.id == r.companyId) myCo = ci;
            bool leader = myCo != null && string.Equals(myCo.leader, me, StringComparison.OrdinalIgnoreCase);
            GUILayout.Space(6);
            GUILayout.Label("<b>Ma compagnie</b>");
            if (myCo != null)
            {
                GUILayout.Label("   « " + myCo.name + " »  (" + myCo.members.Count + "/" + r.max + ")  —  " + (leader ? "vous êtes le chef" : "chef : " + myCo.leader) +
                                "  —  allégeance : " + (myCo.career.Length > 0 ? CareerLabelOf(db, myCo.career) : "<color=#ff9f7f>non affectée</color>"));
                foreach (string m in myCo.members)
                {
                    bool isLead = string.Equals(m, myCo.leader, StringComparison.OrdinalIgnoreCase), isMe = string.Equals(m, me, StringComparison.OrdinalIgnoreCase);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("      " + m + (isLead ? "  (chef)" : "") + (isMe ? "  (vous)" : ""), GUILayout.Width(300));
                    if (leader && !isMe)
                    {
                        if (GUILayout.Button(" Nommer chef ", GUILayout.Width(110))) ReputationManager.CompanyCmd("transfer", m, "");
                        if (GUILayout.Button(" Renvoyer ", GUILayout.Width(90))) ReputationManager.CompanyCmd("kick", m, "");
                    }
                    GUILayout.EndHorizontal();
                }
                if (leader)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("   Nom du joueur :", GUILayout.Width(130));
                    _coTarget = GUILayout.TextField(_coTarget ?? "", 32, GUILayout.Width(200));
                    GUI.enabled = _coTarget.Trim().Length > 0;
                    if (GUILayout.Button(" Ajouter ", GUILayout.Width(90))) ReputationManager.CompanyCmd("invite", _coTarget.Trim(), "");
                    if (GUILayout.Button(" Renvoyer ", GUILayout.Width(90))) ReputationManager.CompanyCmd("kick", _coTarget.Trim(), "");
                    if (GUILayout.Button(" Transmettre le rang de chef ", GUILayout.Width(210))) { ReputationManager.CompanyCmd("transfer", _coTarget.Trim(), ""); _coTarget = ""; }
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                    if (db != null)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("   Affecter la compagnie à :", GUILayout.Width(190));
                        foreach (KeyValuePair<string, CareerDefinition> kv in db.careers)
                        {
                            GUI.enabled = !string.Equals(kv.Key, myCo.career, StringComparison.OrdinalIgnoreCase);
                            if (GUILayout.Button(FactionLabel(kv.Value.faction), GUILayout.Width(125))) ReputationManager.CompanyCmd("setcareer", kv.Key, "");
                            GUI.enabled = true;
                        }
                        GUILayout.EndHorizontal();
                        GUILayout.Label("<color=#99aaaa>   Les membres sans allégeance héritent de celle de la compagnie.</color>");
                    }
                    if (GUILayout.Button(" Dissoudre la compagnie ", GUILayout.Width(190))) ReputationManager.CompanyCmd("disband", "", "");
                }
                else if (GUILayout.Button(" Quitter la compagnie ", GUILayout.Width(190))) ReputationManager.CompanyCmd("leave", "", "");
            }
            else
            {
                if (r.inviteCompany.Length > 0)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("   " + r.inviter + " vous invite dans « " + r.inviteCompany + " ».", GUILayout.Width(420));
                    if (GUILayout.Button(" Accepter ", GUILayout.Width(100))) ReputationManager.CompanyCmd("accept", "", "");
                    GUILayout.EndHorizontal();
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label("   Fonder une compagnie :", GUILayout.Width(190));
                _coName = GUILayout.TextField(_coName ?? "", 24, GUILayout.Width(220));
                if (GUILayout.Button(" Fonder ", GUILayout.Width(90)) && _coName.Trim().Length >= 3) { ReputationManager.CompanyCmd("create", _coName.Trim(), ""); _coName = ""; }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.Label("<b>Membres de ma faction</b>  (" + r.members.Count + ")");
            foreach (RosterMember m in r.members)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("   " + m.name + (string.Equals(m.name, me, StringComparison.OrdinalIgnoreCase) ? "  (vous)" : ""), GUILayout.Width(260));
                GUILayout.Label(m.online ? "<color=#8fe08f>en ligne</color>" : "<color=#888888>hors ligne</color>", GUILayout.Width(90));
                GUILayout.Label(m.company.Length > 0 ? "« " + m.company + " »" : "<color=#888888>sans compagnie</color>", GUILayout.Width(200));
                GUILayout.Label("PvP " + m.kills + " / " + m.deaths, GUILayout.Width(90));
                if (leader && m.online && m.company.Length == 0 && myCo.members.Count < r.max &&
                    !string.Equals(m.name, me, StringComparison.OrdinalIgnoreCase) && GUILayout.Button(" Inviter ", GUILayout.Width(90)))
                    ReputationManager.CompanyCmd("invite", m.name, "");
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(6);
            GUILayout.Label("<b>Compagnies connues</b>  (" + r.companies.Count + ")");
            foreach (CompanyInfo ci in r.companies)
                GUILayout.Label("   « " + ci.name + " »  —  " + (ci.career.Length > 0 ? CareerLabelOf(db, ci.career) : "allégeance non affectée") + "  —  chef " + ci.leader + "  —  " + ci.members.Count + " membre(s)");
            if (r.companies.Count == 0) GUILayout.Label("   <color=#888888>Aucune compagnie n'a encore été fondée.</color>");
        }

        /// <summary>Career rank (careers.&lt;id&gt;.ranks, on the points of the career's faction) and campaign progress (months handed in).</summary>
        private static void DrawCareerBars(Player lp, ManagerNPCRP.NpcDatabase db, string career, RepSnapshot snap)
        {
            CareerDefinition cd;
            if (!db.careers.TryGetValue(career, out cd) || cd == null) return;
            JArray ranks = null; JToken rt;
            if (cd.extra != null && cd.extra.TryGetValue("ranks", out rt)) ranks = rt as JArray;
            GUILayout.Label("<b>" + (string.IsNullOrEmpty(cd.name) ? career : cd.name) + "</b>");
            if (ranks != null && ranks.Count > 0)
            {
                float pts = 0f; bool known = false;
                if (snap != null) { float v; if (snap.factions.TryGetValue(cd.faction, out v)) pts = v; known = true; }
                int idx = 0; float top = 1f;
                for (int i = 0; i < ranks.Count; i++)
                {
                    float need = (float)ranks[i]["points"];
                    if (pts >= need) idx = i;
                    top = Mathf.Max(top, need);
                }
                List<float> marks = new List<float>();
                for (int i = 1; i < ranks.Count; i++) marks.Add((float)ranks[i]["points"] / top);
                string title = (string)ranks[idx]["title"];
                string next = idx + 1 < ranks.Count ? (string)ranks[idx + 1]["title"] + " à " + (float)ranks[idx + 1]["points"] + " pts" : "rang maximal";
                GUILayout.BeginHorizontal();
                GUILayout.Label("Rang : <b>" + title + "</b>" + (known ? "   (" + Mathf.RoundToInt(pts) + " pts)" : ""), GUILayout.Width(330));
                Bar(known ? Mathf.Clamp01(pts / top) : 0f, new Color(0.85f, 0.7f, 0.25f), 280f, 14f, marks.ToArray());
                GUILayout.Label(known ? "  → " + next : "  <color=#99aaaa>(points en attente du serveur)</color>");
                GUILayout.EndHorizontal();
            }
            int done = 0, total = 0;
            foreach (QDef q in QuestEngine.Ordered)
            {
                if (!q.career || !q.careers.Any(x => string.Equals(x, career, StringComparison.OrdinalIgnoreCase))) continue;
                total++;
                if (QuestEngine.IsCompleted(lp, q.id)) done++;
            }
            if (total > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Campagne : <b>" + done + "/" + total + "</b> mois rendus", GUILayout.Width(330));
                Bar(done / (float)total, new Color(0.45f, 0.78f, 0.5f), 280f, 14f);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(4);
        }

        private static void DrawRepRow(string label, bool isFaction, string key, RepSnapshot snap, ReputationSettings r, Player lp, string career)
        {
            int t1 = isFaction ? r.factionTier1 : r.tier1, t2 = isFaction ? r.factionTier2 : r.tier2, t3 = isFaction ? r.factionTier3 : r.tier3;
            int hostileBelow = isFaction ? r.factionHostileBelow : r.hostileBelow;
            int tier = isFaction ? ReputationManager.FactionTierFromFlags(key) : ReputationManager.TierFromFlags(key);
            bool hostile = DialogueStages.FlagOn((isFaction ? "repfhostile_" : "rephostile_") + key);
            float pts = float.NaN;
            if (snap != null)
            {
                float v;
                Dictionary<string, float> src = isFaction ? snap.factions : snap.clans;
                pts = src.TryGetValue(key, out v) ? v : 0f;
            }
            bool known = !float.IsNaN(pts);
            if (known)
            {
                hostile = hostile || pts <= hostileBelow;
                tier = hostile ? 0 : (pts >= t3 ? 3 : pts >= t2 ? 2 : pts >= t1 ? 1 : 0);
            }
            bool neg = known && pts < 0f && !hostile;
            Color col = hostile ? new Color(1f, 0.45f, 0.4f) : neg ? new Color(1f, 0.65f, 0.3f) : TierColor(tier);
            float frac = known
                ? (pts < 0f ? Mathf.Clamp01(-pts / Mathf.Max(1f, -hostileBelow)) : Mathf.Clamp01(pts / Mathf.Max(1, t3)))
                : tier / 3f;
            float[] ticks = known && pts < 0f ? new float[0] : new[] { t1 / (float)Mathf.Max(1, t3), t2 / (float)Mathf.Max(1, t3) };

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(150));
            Color old = GUI.contentColor;
            GUI.contentColor = col;
            GUILayout.Label(hostile ? "HOSTILE" : neg ? "Défavorable" : QuestEngine.TierName(tier), GUILayout.Width(100));
            GUI.contentColor = old;
            Bar(hostile ? 1f : frac, col, 170f, 12f, ticks);
            string info = known ? (pts > 0f ? "+" : "") + Mathf.RoundToInt(pts) + " pts" : "";
            if (known && !hostile && pts < 0f) info += "  → hostile à " + hostileBelow;
            else if (known && !hostile && tier < 3) info += "  → " + QuestEngine.TierName(tier + 1) + " à " + (tier == 0 ? t1 : tier == 1 ? t2 : t3);
            GUILayout.Label(info);
            GUILayout.EndHorizontal();

            // quests behind this reputation
            int done = 0, total = 0;
            foreach (QDef q in QuestEngine.Ordered)
            {
                if (QuestEngine.HiddenForPlayer(q)) continue;
                if (isFaction ? q.faction != key : q.clan != key) continue;
                total++;
                if (QuestEngine.IsCompleted(lp, q.id)) done++;
            }
            int qpts = QuestEngine.QuestRepPoints(isFaction ? null : key, isFaction ? key : null, career);
            if (total > 0 || qpts != 0)
                GUILayout.Label("<color=#99aaaa>      Quêtes faites : " + done + "/" + total +
                                (qpts != 0 ? "   —   issu des quêtes / événements : " + (qpts > 0 ? "+" : "") + qpts : "") + "</color>");
        }

        // ---- game input patches (applied through ManagerNPCRP.Awake, wrapped in try/catch there)
        internal static void TakeInputPostfix(ref bool __result)
        {
            if (_open)
                __result = false;
        }
        internal static bool MouseCapturePrefix()
        {
            if (!_open)
                return true;

            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            return false;
        }

        // Clears Unity input immediately before Player.Update while the quest window is open.
        internal static bool GetButtonPrefix(string name, ref bool __result)
        {
            if (!_open)
                return true;

            switch (name)
            {
                case "Attack":
                case "AttackSecondary":
                case "Block":
                case "Use":
                    __result = false;
                    return false;
            }

            return true;
        }

        internal static bool GetButtonDownPrefix(string name, ref bool __result)
        {
            if (!_open)
                return true;

            switch (name)
            {
                case "Attack":
                case "AttackSecondary":
                case "Block":
                case "Use":
                    __result = false;
                    return false;
            }

            return true;
        }

        internal static void PlayerUpdatePrefix()
        {
            if (!_open)
                return;

            Input.ResetInputAxes();

            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
    }
}
