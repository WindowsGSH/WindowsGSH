# WindowsGSH Blank Steam Template

This template is a starting point for native WindowsGSH modules that install through SteamCMD.

Copy this folder, rename it for your game, then edit the importable `.mod` folder inside it. The template is valid enough to import, but it is not a real game server module until you replace the placeholder Steam app id, executable path, config fields, launch arguments, and any game-specific C# behavior.

## Folder Layout

Recommended module repository layout:

```text
WindowsGSH.MyGame/
  README.md
  LICENSE.md
  .gitignore
  MyGame.mod/
    module.json
    MyGameModule.cs
    author.png
```

WindowsGSH can import either the `.mod` folder directly or a repository/root folder containing a nested `.mod` folder. The import process copies the module payload and skips development folders such as `.git`, `bin`, and `obj`.

This template uses:

```text
BlankSteamTemplate/
  README.md
  LICENSE.md
  BlankSteamTemplate.mod/
    module.json
    BlankSteamTemplateModule.cs
    author.png
```

## Module Management Display

The Modules page reads these files and manifest fields:

- `author.png`: author/module image. Keep it square if possible; WindowsGSH displays it at a fixed size.
- `name`: friendly module name.
- `author`: module author or organisation.
- `description`: short module summary.
- `version`: module version.
- `url`: primary link shown in the module card.
- `homepage`, `repository`, `sourceUrl`: fallback/provenance links.
- `color`: hex accent colour behind `author.png`, for example `#1E8449`.

## First Changes To Make

In `BlankSteamTemplate.mod/module.json`, change:

- `id`
- `name`
- `author`
- `description`
- `url`
- `homepage`
- `repository`
- `sourceUrl`
- `color`
- `steam.appId`
- `entryPoints.start`
- `entryPoints.processName`
- `runtime.defaultArguments`
- `configFields`
- `backupTargets`

If you keep the C# file, also rename:

- `BlankSteamTemplate.mod/BlankSteamTemplateModule.cs`
- the namespace in the `.cs` file
- the class name in the `.cs` file
- `module.json` `entry`

Example:

```text
Folder: WindowsGSH.Icarus/Icarus.mod
Manifest id: icarus
C# file: IcarusModule.cs
C# class: IcarusModule
Manifest entry: WindowsGSH.Modules.Icarus.IcarusModule
```

## Manifest Basics

Required for all modules:

- `id`: unique lowercase id, used for installed module folders and server `moduleId`.
- `name`: friendly display name.
- `entryPoints.start`: executable path relative to the installed server files folder.

Required for C# modules:

- `type`: `csharp`.
- `entry`: fully qualified class name implementing `IGameServerModule`.

Steam modules should include:

```json
"steam": {
  "appId": "123456",
  "anonymous": true,
  "validate": true
}
```

Remove `steam` only for non-Steam servers. This template is intentionally Steam-focused; create a separate non-Steam template if the install flow is different.

`steam.customArguments` is only for exceptional module-authored SteamCMD option tokens. WindowsGSH parses it with Windows command-line quoting and rejects additional `+commands` such as `+quit` or `+force_install_dir`; use the dedicated Steam manifest fields wherever possible.

## Common Manifest Fields

- `runtime.consoleStrategy`: use `Redirected`, `WindowMessage`, `RconPreferred`, `LogTailOnly`, or `None`. `Redirected` is the correct starting point for most Steam dedicated servers. The legacy `allowsEmbeddedConsole` field is a compatibility fallback; prefer `consoleStrategy`.
- `runtime.logPath`: optional path relative to the server install folder where the game writes log files, for example `logs`.
- `runtime.portIncrements`: how many ports one server consumes when WindowsGSH proposes the next server port.
- `runtime.defaultArguments`: launch arguments with placeholders such as `{network.port}` and `{server.name}`.
- `capabilities.directConnection`: whether users connect directly to an IP/port.
- `capabilities.consoleCommands`: set to `true` only when `consoleStrategy` is `WindowMessage` and the C# module implements `IModuleConsoleCommandCapability`.
- `configFields`: install/config UI fields stored in the server config.
- `backupTargets`: folders/files included in backups.
- `addons`: optional addon metadata.
- `addons[].package`: optional explicit addon automation for `Zip`, `Tar`, `TarGz`, or direct `File` downloads.
- `addons[].package.installPath`: server-files-relative destination. Paths are traversal-checked.
- `addons[].package.stripComponents` / `archiveSubpath`: trim archive wrappers before installation.
- `addons[].package.requiredMarkers`: files/folders that must exist in the staged package before WindowsGSH changes the server.
- `addons[].package.expectedSha256`: strongly recommended. SHA-256 of the downloaded archive/file, verified before extraction and the install rejected on mismatch. Leave blank only if you can't pin a hash yet — WindowsGSH surfaces a module diagnostics warning for addon packages without one, and installs proceed without verifying the download.
- `addons[].sourceName` / `sourceVersion`: provenance shown in addon status and stored with the install record.
- Automated addon installs are user-triggered. WindowsGSH records installed files, backs up overwritten files, and rolls back partial failures.

## Config Field Types

Supported types for `configFields`:

- `Text`
- `Password`
- `Number`
- `Boolean`
- `Select`
- `MultiSelect`
- `Path`
- `Port`
- `Cron`
- `CommandLine`

Use dotted keys such as `server.name`, `network.port`, `network.queryPort`, `rcon.password`, and `server.additionalArguments`.

### Advanced Config Field Options

Each field also supports these optional properties:

- `group`: string label that groups related fields together in the settings UI.
- `restartRequired`: `true` when changing the value requires a server restart to take effect.
- `validationPattern`: regex string that the value must match.
- `validationMessage`: message shown when `validationPattern` is not satisfied.
- `visibleWhen`: object with `key` (another field's key) and either `equals` (string comparison) or `isTruthy` (boolean check). Hides the field when the condition is not met.

Example — show a password field only when the server is set to private:

```json
{
  "key": "server.password",
  "label": "Server Password",
  "type": "Password",
  "defaultValue": "",
  "visibleWhen": { "key": "server.public", "isTruthy": false }
}
```

## C# Module Checklist

Only keep a C# module when the game needs custom behavior. Manifest-only modules are fine for simple servers.

When turning the template into a real C# module:

1. Rename the namespace and class.
2. Update `module.json` `entry` to match.
3. Replace `steam.appId`.
4. Replace `entryPoints.start` and process names.
5. Update `WriteConfigFileSettingsAsync` to write the game's actual config files.
6. Update `CreateStartInfoAsync` only if the default launch behavior is not enough.
7. Implement `QueryAsync` only when the game has a real query protocol.
8. Implement `ExecuteRconCommandAsync` only when the game supports RCON or a command API.
9. Set `Manifest.ToCapabilities(supportsQuery: true, supportsRcon: true)` only for features you actually implement.

## Launch Arguments

Placeholders in `runtime.defaultArguments` are replaced from the server's saved settings before launch:

```json
"runtime": {
  "defaultArguments": "-ip {network.ip} -port {network.port} -maxplayers {server.maxPlayers} {server.additionalArguments}"
}
```

WindowsGSH replaces each `{key}` with the value stored for that config field key.

## Backups

Backup targets should be relative to the server install folder. Prefer game-native folders such as `config`, `saves`, `world`, `Saved`, or `Saved/Config`.

Mark a target `required` only when it is essential.

## Importing During Development

In WindowsGSH, open Module Management and choose either:

- `Import Folder`, then select the `.mod` folder or the repository root containing it.
- `Import ZIP`, then select a ZIP containing the `.mod` folder.

Installed modules are copied into:

```text
modules/installed/<module-id>/
```

This path is relative to the WindowsGSH executable. Edit your source template, then reimport when you want WindowsGSH to use the updated copy.

## Trust Note

C# modules run code on the user's machine. WindowsGSH does not create, own, review, sign, or guarantee third-party modules. Module authors should keep code readable, document what the module does, and avoid surprising side effects.
