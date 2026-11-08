[Uploading README.md…]()
# Valheim LAN Backend

Plugin BepInEx pour relier un hôte et ses clients par TCP sur un réseau local ou
Radmin. Le plugin doit être installé sur l'hôte et sur chaque client.

## Connexion

- L'hôte démarre son monde comme d'habitude. Le plugin installe le socket TCP
  `ZSocket2` de Valheim sur le port hôte et journalise les adresses locales.
- Sur chaque client, renseigner `ManualServerIP` avec l'adresse LAN/Radmin de
  l'hôte et `ManualServerPort` avec le port TCP affiché dans son log.
- Appuyer sur F9 depuis le menu Valheim. Si `ManualServerIP` est vide, le plugin
  tente d'abord la découverte UDP ; elle peut ne pas traverser un réseau Radmin,
  auquel cas l'adresse de l'hôte doit être saisie manuellement.

Le trafic de jeu reste encadré par le protocole TCP natif de Valheim. Ce plugin
ne fournit pas de relais public ni de VPN.

## Compilation

Lancer `buildlan.bat`. Le dossier par défaut est `E:\Games\Valheim`; il peut
être remplacé par la variable `VALHEIM_DIR` ou la propriété MSBuild
`-p:ValheimDir=...`. Par défaut, les références sont cherchées ici :

- `valheim_Data/Managed` : `assembly_valheim.dll` et les modules Unity utilisés ;
- `BepInEx/core` : `BepInEx.dll` et `0Harmony.dll` ;
- `BepInEx/Plugins` : `assembly_steamworks.dll`.
- `valheim_Data/Managed` : `assembly_utils.dll` et `com.rlabrecque.steamworks.net.dll`.

Pour une installation non standard, définir aussi `VALHEIM_MANAGED_DIR`,
`BEPINEX_CORE_DIR` ou `BEPINEX_PLUGINS_DIR`. Le script produit la DLL sous
`bin/Release/netstandard2.1/ValheimLANBackend.dll` et ne l'installe pas
automatiquement.
