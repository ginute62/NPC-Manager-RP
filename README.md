# Norseman NPC RP

**Norseman NPC RP** is a BepInEx mod for *Valheim* that turns Norsemen NPCs into persistent roleplay characters: identity, clan, faction, dialogue, quests, reputation, PvP, companies, and dynamic population.

All mod content is driven by one JSON database:

```text
BepInEx/config/Norseman-NPC-RP/npcdatabase.json
```

On first launch, if this file does not exist, the plugin automatically creates an empty template (`WriteTemplateDatabase`).

## How it works

```text
npcdatabase.json
        │
        ▼
ManagerNPCRP.LoadDatabase()
        │
        ├── Validates schema and references
        ├── Migrates schema v2 to v3 if needed
        ├── Loads data into memory (NpcDatabase)
        └── Initializes game systems
                ├── Spawner / ZDO
                ├── Dialogue
                ├── Quests
                ├── Reputation / careers
                ├── PvP / companies
                └── Generated population
```

The mod is organized around `ManagerNPCRP`, a BepInEx `BaseUnityPlugin`. It loads the JSON, validates it, creates or updates NPCs in the world, and provides the loaded database to the other systems.

## Database file

The database is a versioned JSON document. The version currently expected by the code is:

```json
"schemaVersion": 3
```

If the file uses schema version 2, the plugin attempts to migrate it to version 3, creates a `.v2.bak` backup, and saves the migrated database.

### Main sections

| Section | Purpose |
|---|---|
| `settings` | General options: enablement, spawning, duplicate prevention, respawn, debug, placement. |
| `factions` | Faction definitions and their aggressive variants. |
| `clans` | Clan definitions, linked to a faction and optionally to a default dialogue set. |
| `equipmentSets` | Reusable equipment sets for NPCs. |
| `dialogueSets` | Dialogue lines, conditions, reactions, and quest states. |
| `npcs` | Named NPC definitions: identity, prefab, clan, position, behavior, dialogue, equipment. |
| `careers` | Player careers/allegiances, faction, enemies, monthly quests, and rewards. |
| `reputation` | Reputation rules and rewards linked to flags. |
| `cityZones` | City zones: center, wander radius, capacity, and professions. |
| `population` | Rules for generated population, immigration, births, groups, and population limits. |
| `groupBehavior` | Behavior rules for generated NPC groups. |
| `quests` | Side quests, deliveries, escorts, investigations, and other content. |
| `questLines` | Career objectives, narrative calendar, and flag-based objectives. |

The code validates that every NPC has an ID, name, prefab, and existing clan. It also verifies that referenced factions, equipment sets, and dialogue sets actually exist.

## NPCs defined in JSON

Each entry in `npcs` describes a named NPC. The plugin uses its ID as a persistent identity in the world through ZDO data.

```json
"NPC-001": {
  "identity": {
    "name": "NPC Name"
  },
  "prefab": "NorsemanRS",
  "clan": "MyClan",
  "faction": "MyFaction",
  "role": "Guard",
  "equipmentSet": "",
  "dialogue": {
    "set": "MyClanDialogue"
  },
  "locations": {
    "spawn": {
      "enabled": true,
      "x": 0.0,
      "y": 0.0,
      "z": 0.0,
      "rotationY": 0.0,
      "baked": false
    }
  }
}
```

The `prefab` field must match a prefab available in `ZNetScene`. The plugin then creates a persistent ZDO and writes the RP ID, clan, role, and, when needed, `Living Norsemen` routine data.

## NPC lifecycle

1. The plugin loads and validates `npcdatabase.json`.
2. Defined NPCs are placed into an in-memory dictionary.
3. When the world loads, the plugin searches for existing ZDOs carrying the RP ID.
4. If the NPC is missing, it is created at its defined position.
5. If `preventDuplicates` is enabled, duplicate NPCs are not created.
6. If the NPC is dead, the respawn system decides whether it can return.
7. The `rpaudit fix` command can remove duplicate ZDOs.

## Dialogue and quests

Dialogue is organized into sets (`dialogueSets`). Each set contains stages (`stages`) activated by conditions:

```json
{
  "id": "example",
  "requires": "exampleFlag",
  "forbids": "conflictingFlag",
  "replace": true,
  "Greets": [ "Hello, traveler." ],
  "Farewells": [ "Safe travels." ],
  "Generic": [ "What do you need?" ]
}
```

Quests use progression flags. The main patterns are:

- `qactive<Id>`: quest accepted.
- `qready<Id>`: objectives completed, ready to turn in.
- `qdone<Id>`: quest turned in.
- `career<Id>`: player has selected an allegiance.
- `month<N>`: narrative calendar reached.
- `rep<...>`: reputation tier.
- `rephostile<...>`: hostile reputation.

Objectives can be of type `talk`, `visit`, `kill`, `killnpc`, `deliver`, `collect`, `auto`, or `flag`.

## Reputation, PvP, and companies

The reputation system is linked to careers. Each career has a faction, optional enemy factions, and can gain or lose reputation points based on player actions.

PvP uses these rules:

- Two players with the same allegiance cannot damage each other when friendly fire is disabled.
- Killing a player from an enemy faction grants reputation to the killer’s faction.
- The victim loses reputation in their own faction.
- Coins may be awarded to the killer.
- Anti-farming prevents repeatedly farming the same killer/victim pair.

Companies group players who share an allegiance. They can be created, joined, left, led, transferred, or disbanded through the `rpcompany` commands.

## Generated population

The population system can create generic citizens in cities. It runs in cycles based on `population.tickSeconds` and respects global, faction, and city limits.

It manages:

- Immigration.
- Births.
- Citizen groups.
- Professions.
- Population limits.
- Player-proximity activation.
- Persistent population state in `populationstate.json`.

## Useful commands

| Command | Purpose |
|---|---|
| `spawnrpnpcs` | Spawns NPCs defined in the database. |
| `spawnrpnpcs force` | Replaces existing NPCs instead of creating duplicates. |
| `rpcheck` | Diagnoses NPC placement problems. |
| `rpcheck fix` | Attempts to replace badly placed NPCs. |
| `rpscan` | Finds and saves valid positions near the player. |
| `rpscan dry` | Simulates placement without modifying the JSON. |
| `rpflag set <flag>` | Sets a world-wide story flag. |
| `rpcompany ...` | Manages companies. |
| `rpcareer ...` | Manages the player’s allegiance. |

## Generated and used files

```text
BepInEx/config/Norseman-NPC-RP/
├── npcdatabase.json          RP content database
├── npcdatabase.json.v2.bak   Backup created during a v2 to v3 migration
└── populationstate.json      World population state
```

The database path is built from the BepInEx configuration folder and the file name `npcdatabase.json`.

## Calls between the database and the code

### Principle

`npcdatabase.json` is the **single source of truth for content**.  
The C# code is the **execution engine**: it reads, validates, caches, and applies the data to the game world.

```text
JSON (content)
    │
    ├── LoadDatabase()
    ├── ValidateDatabase()
    ├── BuildFactionCache()
    ├── CareerManager.Validate()
    ├── QuestEngine.Load()
    ├── PopulationManager
    └── Spawn / Dialogue / Reputation / PvP
```

### Initial load

```text
ManagerNPCRP.Awake()
        │
        ▼
LoadDatabase()
        │
        ├── Checks that the file exists
        ├── Creates a template if it is missing
        ├── Reads the JSON
        ├── Deserializes it into NpcDatabase
        ├── Migrates v2 to v3 if needed
        ├── Validates references
        ├── Fills ManagerNPCRP.npcs
        ├── Builds the faction cache
        └── Initializes systems
```

At this point, the database is no longer just a file: it becomes an `NpcDatabase` object accessible through `ManagerNPCRP.Database`.

### Reference validation

The code checks links between sections:

```text
npc.clan ──────────────► clans
clan.faction ──────────► factions
npc.equipmentSet ──────► equipmentSets
npc.dialogue.set ──────► dialogueSets
faction.aggressiveVariant ► factions
career.faction ─────────► factions
career.enemyFactions ───► factions
quest.npc ─────────────► npcs
quest.clan ────────────► clans
```

If a reference is invalid, validation fails and no NPC is spawned.

### Calls from the spawner

```text
SpawnAllConfiguredNPCs()
        │
        ├── CollectZdosById()
        ├── Checks duplicates
        ├── Checks respawn state
        └── SpawnNpcZdo()
                │
                ├── Gets the prefab
                ├── Calculates the position
                ├── Creates the ZDO
                ├── Writes the RP identity
                ├── Writes clan / role / profession
                └── Applies Living Norsemen data
```

The JSON provides:

- `npcs.<id>.prefab`
- `npcs.<id>.locations.spawn`
- `npcs.<id>.clan`
- `npcs.<id>.role`
- `npcs.<id>.livingNorsemen`

The code converts this data into a persistent ZDO.

### Calls from dialogue

```text
Player interacts with an NPC
        │
        ▼
NpcTalk.TryTalk()
        │
        ├── Finds the NPC through its ZDO
        ├── Finds NpcDefinition through the RP ID
        ├── Finds DialogueSetDefinition
        ├── Evaluates requires / forbids
        ├── Checks player and world flags
        └── Displays the appropriate line
```

The JSON provides dialogue lines, conditions, and reactions. The code decides which entry is visible according to the current game state.

### Calls from quests

```text
QuestEngine.Load()
        │
        ├── Reads quests
        ├── Reads questLines
        ├── Reads careers.*.calendar
        └── Builds available quests

Player accepts a quest
        │
        ├── Sets qactive<Id>
        ├── Tracks objectives
        ├── Sets qready<Id>
        └── On turn-in: sets qdone<Id>

Quest turn-in
        │
        ├── Optionally removes items
        ├── Grants rewards
        ├── Applies reputation gains/losses
        └── Sets required narrative flags
```

Objectives can target an NPC, clan, area, creatures, items, or narrative flags.

### Calls from reputation

```text
Player action
        │
        ├── Kill / death / interaction / quest
        ▼
ReputationManager
        │
        ├── Reads careers.<id>.faction
        ├── Reads reputation.rewards
        ├── Adds or removes points
        ├── Updates reputation tiers
        └── Triggers reputation dialogue
```

Reputation tiers can unlock dialogue stages such as `rep1`, `rep2`, `rep3`, or `hostile`.

### PvP and company calls

```text
PvP death
        │
        ├── Identifies killer and victim
        ├── Reads their careers
        ├── Reads their factions
        ├── Checks allegiance / enemies
        ├── Applies reputation and coins
        └── Saves state

Company command
        │
        ├── Reads careers.<id>.faction
        ├── Checks members
        ├── Creates / modifies / disbands the company
        ├── Propagates allegiance
        └── Saves state
```

Careers act as the bridge between JSON content and PvP/company rules.

### Population calls

```text
Population tick
        │
        ├── Reads population
        ├── Reads cityZones
        ├── Reads clans / factions
        ├── Counts existing citizens
        ├── Applies limits
        ├── Generates immigration or births
        └── Spawns citizens
```

Generated population is not necessarily defined NPC by NPC: it is dynamically produced from `population`, `cityZones`, and `groupBehavior` rules.

### Responsibility summary

| Element | Provides | Consumes |
|---|---|---|
| `npcdatabase.json` | Content, identities, positions, text, rules | Is read and validated |
| `ManagerNPCRP` | Loading, validation, cache, spawning | JSON |
| `NpcTalk` | Dialogue | `dialogueSets`, flags |
| `QuestEngine` | Quests, objectives, rewards | `quests`, `questLines`, `careers` |
| `ReputationManager` | Reputation, hostility, PvP | `careers`, `reputation` |
| `PopulationManager` | Generated citizens | `population`, `cityZones`, `clans` |
| ZDO / Valheim | World persistence | Data written by the plugin |

## Minimal working JSON example

Save this as `BepInEx/config/Norseman-NPC-RP/npcdatabase.json` to test the full pipeline with one faction, one clan, one dialogue set, and one NPC.

```json
{
  "schemaVersion": 3,

  "settings": {
    "enabled": true,
    "spawnOnWorldLoad": false,
    "preventDuplicates": true,
    "respawnIfDead": false,
    "respawnDelaySeconds": 1800.0,
    "debug": true,
    "snapToGround": true,
    "spawnHeightOffset": 0.5,
    "defaultPlayerFaction": "",
    "placement": {
      "scanMaxPlayerDistance": 50.0,
      "maxSlope": 35.0,
      "minGroundClearance": 1.0,
      "avoidWater": true,
      "avoidColliders": true,
      "minSpacing": 2.0,
      "searchRadius": 8.0,
      "maxAttempts": 40
    }
  },

  "factions": {
    "MyFaction": {
      "name": "My Faction",
      "isFriendly": true,
      "aggressiveVariant": ""
    }
  },

  "clans": {
    "MyClan": {
      "name": "My Clan",
      "faction": "MyFaction",
      "defaultDialogueSet": "MyClanDialogue"
    }
  },

  "equipmentSets": {},

  "dialogueSets": {
    "MyClanDialogue": {
      "stages": [
        {
          "id": "base",
          "requires": "",
          "forbids": "",
          "replace": false,
          "Greets": [
            "Hello, traveler."
          ],
          "Farewells": [
            "Safe travels."
          ],
          "Generic": [
            "What can I do for you?"
          ],
          "PlayerBase": [],
          "Damaged": [],
          "Thieved": [],
          "Puke": [],
          "Eat": []
        }
      ]
    }
  },

  "npcs": {
    "NPC-001": {
      "identity": {
        "name": "Test Guard",
        "gender": "Male"
      },
      "prefab": "NorsemanRS",
      "clan": "MyClan",
      "faction": "MyFaction",
      "repFaction": "",
      "role": "Guard",
      "livingNorsemen": {
        "profession": "Guard"
      },
      "equipmentSet": "",
      "locations": {
        "spawn": {
          "enabled": true,
          "x": 0.0,
          "y": 0.0,
          "z": 0.0,
          "rotationY": 0.0,
          "baked": false
        },
        "home": {
          "enabled": false,
          "x": 0.0,
          "y": 0.0,
          "z": 0.0,
          "rotationY": 0.0,
          "baked": false
        },
        "work": {
          "enabled": false,
          "x": 0.0,
          "y": 0.0,
          "z": 0.0,
          "rotationY": 0.0,
          "baked": false
        },
        "patrolPointB": {
          "enabled": false,
          "x": 0.0,
          "y": 0.0,
          "z": 0.0,
          "rotationY": 0.0,
          "baked": false
        }
      },
      "behavior": {
        "isStatic": false,
        "aggressive": false
      },
      "dialogue": {
        "set": "MyClanDialogue"
      }
    }
  },

  "careers": {},

  "reputation": {
    "repFactions": [],
    "cityRadius": 60.0,
    "monthDays": 8.0,
    "rewards": {},
    "texts": {},
    "careerHostility": {
      "enabled": true,
      "guardsOnly": true
    }
  },

  "cityZones": {},

  "population": {
    "enabled": false,
    "tickSeconds": 300.0,
    "maxGeneratedTotal": 120,
    "requirePlayerNearby": true,
    "activationRadius": 160.0,
    "reproductionEnabled": true,
    "factions": {},
    "names": {
      "male": [],
      "female": [],
      "epithets": {}
    }
  },

  "groupBehavior": {
    "enabled": true,
    "maxGroupSize": 4,
    "groupChance": 0.5,
    "followRadius": 5.0,
    "wanderMinSeconds": 25.0,
    "wanderMaxSeconds": 75.0,
    "wanderGenerated": true,
    "wanderNamed": false
  },

  "quests": {},

  "questLines": {
    "careerObjectives": {}
  }
}
```

The `NorsemanRS` prefab must exist in your installation; otherwise, the plugin logs a “prefab not found” error and does not create the NPC.

## Empty database template

Use this template to start a new, empty RP database:

```json
{
  "schemaVersion": 3,

  "settings": {
    "enabled": true,
    "spawnOnWorldLoad": false,
    "preventDuplicates": true,
    "respawnIfDead": false,
    "respawnDelaySeconds": 1800.0,
    "debug": true,
    "snapToGround": true,
    "spawnHeightOffset": 0.5,
    "defaultPlayerFaction": "",
    "placement": {
      "scanMaxPlayerDistance": 50.0,
      "maxSlope": 35.0,
      "minGroundClearance": 1.0,
      "avoidWater": true,
      "avoidColliders": true,
      "minSpacing": 2.0,
      "searchRadius": 8.0,
      "maxAttempts": 40
    }
  },

  "factions": {},
  "clans": {},
  "equipmentSets": {},
  "dialogueSets": {},
  "npcs": {},

  "careers": {},

  "reputation": {
    "repFactions": [],
    "cityRadius": 60.0,
    "monthDays": 8.0,
    "rewards": {},
    "texts": {},
    "careerHostility": {
      "enabled": true,
      "guardsOnly": true
    }
  },

  "cityZones": {},

  "population": {
    "enabled": false,
    "tickSeconds": 300.0,
    "maxGeneratedTotal": 120,
    "requirePlayerNearby": true,
    "activationRadius": 160.0,
    "reproductionEnabled": true,
    "factions": {},
    "names": {
      "male": [],
      "female": [],
      "epithets": {}
    }
  },

  "groupBehavior": {
    "enabled": true,
    "maxGroupSize": 4,
    "groupChance": 0.5,
    "followRadius": 5.0,
    "wanderMinSeconds": 25.0,
    "wanderMaxSeconds": 75.0,
    "wanderGenerated": true,
    "wanderNamed": false
  },

  "quests": {},

  "questLines": {
    "careerObjectives": {}
  }
}
```

This empty template matches the plugin’s expected schema version 3 and contains no NPC, faction, dialogue, quest, or population content.
