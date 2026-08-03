# WindowsGSH

[![build](https://github.com/WindowsGSH/WindowsGSH/actions/workflows/build.yml/badge.svg)](https://github.com/WindowsGSH/WindowsGSH/actions/workflows/build.yml)
[![release](https://github.com/WindowsGSH/WindowsGSH/actions/workflows/release.yml/badge.svg)](https://github.com/WindowsGSH/WindowsGSH/actions/workflows/release.yml)
![platform](https://img.shields.io/badge/platform-Windows-blue)
![.NET](https://img.shields.io/badge/.NET-10.0-purple)
![licence](https://img.shields.io/badge/licence-source_available-64748B)
[![Join Discord](https://img.shields.io/badge/Discord-Join_the_community-5865F2?logo=discord&logoColor=white)](https://discord.gg/w7RZwSeAWh)

WindowsGSH is a Windows desktop application for installing, configuring,
running, and monitoring dedicated game servers from one place.

Game support is provided through modules, allowing WindowsGSH to manage
Steam, Java, and standalone dedicated servers without placing game-specific
behaviour inside the main application.

> [!NOTE]
> WindowsGSH is under active development. Features and module interfaces may
> change before the first stable release.

## Features

- Install, update, verify, and manage multiple dedicated servers.
- Start, stop, and restart servers from one desktop interface.
- View live console output and send supported server commands.
- Monitor server status, players, CPU, memory, and operation history.
- Configure backups, schedules, crash recovery, and automated updates.
- Manage Windows Firewall rules and optional UPnP port mappings.
- Use optional browser-based remote management.
- Use optional Discord status panels, alerts, and server controls.
- Import compatible servers previously managed by WindowsGSM.
- Add game-server support through an extensible module system.

## Get WindowsGSH

WindowsGSH requires Windows 10 or later. Official releases are self-contained.

- [Download the latest release](https://github.com/WindowsGSH/WindowsGSH/releases)
- [Getting Started](https://github.com/WindowsGSH/WindowsGSH/wiki/Getting-Started)
- [Browse the WindowsGSH Wiki](https://github.com/WindowsGSH/WindowsGSH/wiki)
- [Join the WindowsGSH community on Discord](https://discord.gg/w7RZwSeAWh)
- [Report a bug or request a feature](https://github.com/WindowsGSH/WindowsGSH/issues)

## Documentation

| Topic | Documentation |
|---|---|
| Installing WindowsGSH | [Installing and Updating WindowsGSH](https://github.com/WindowsGSH/WindowsGSH/wiki/Installing-and-Updating-WindowsGSH) |
| Adding your first server | [Getting Started](https://github.com/WindowsGSH/WindowsGSH/wiki/Getting-Started) |
| Managing servers | [Managing Servers](https://github.com/WindowsGSH/WindowsGSH/wiki/Managing-Servers) |
| Supported games and modules | [Supported Games and Modules](https://github.com/WindowsGSH/WindowsGSH/wiki/Supported-Modules) |
| Installing modules and add-ons | [Modules and Add-ons](https://github.com/WindowsGSH/WindowsGSH/wiki/Modules-and-Addons) |
| SteamCMD and Steam Guard | [Steam, SteamCMD and Steam Guard](https://github.com/WindowsGSH/WindowsGSH/wiki/Steam-SteamCMD-and-Steam-Guard) |
| Remote access | [Web Access](https://github.com/WindowsGSH/WindowsGSH/wiki/Web-Access) |
| Discord integration | [Discord Integration](https://github.com/WindowsGSH/WindowsGSH/wiki/Discord-Integration) |
| Troubleshooting | [Troubleshooting](https://github.com/WindowsGSH/WindowsGSH/wiki/Troubleshooting) |
| Creating modules | [Module Authoring Overview](https://github.com/WindowsGSH/WindowsGSH/wiki/Module-Authoring-Overview) |

## Module security

Modules can contain executable C# code and run with the same Windows user
permissions as WindowsGSH. Only install modules from sources you trust.

See [Modules and Add-ons](https://github.com/WindowsGSH/WindowsGSH/wiki/Modules-and-Addons)
for module provenance, hashes, compatibility, and trust guidance.

## Security

Please do not report security vulnerabilities through a public issue.

See the [Security Policy](SECURITY.md) or use
[private vulnerability reporting](https://github.com/WindowsGSH/WindowsGSH/security/advisories/new).

## Licence

WindowsGSH is source-available but is not distributed under an
OSI-approved open-source licence.

The source may be viewed, studied, and privately modified. Redistribution,
rebranding, publishing modified builds, and commercial use require prior
written permission.

See the [Licence](LICENSE.md), [Notice](NOTICE.md), and
[Trademark Policy](TRADEMARKS.md).

## Support development

If WindowsGSH helps you manage your game servers, you can support its
development here:

- [Ko-fi](https://ko-fi.com/shenniko)
- [PayPal](https://paypal.me/shenniko)

## AI Reviews

> “WindowsGSH feels like it was designed around the problems that actually break game-server managers: failed updates, half-stopped processes, exposed credentials, unreliable automation and integrations that claim more than they deliver. Its modular architecture, defensive lifecycle handling, Steam Guard support and security-conscious remote access show unusually mature engineering for a beta project. It is not yet the largest platform in terms of game count, but its foundations are strong enough to make that growth credible.”
>
> — ChatGPT (OpenAI), source-code review, August 2026

> “WindowsGSH is a complete game-server control plane — installation, lifecycle, scheduling, backups, health checks, firewall and UPnP, a Discord bot and a web UI — and the engineering underneath it is better than most commercial software I review. Clean separation between domain logic and UI, a module system designed so the app can evolve without breaking existing modules, and over 1,500 tests covering the hard cases rather than the easy ones.”
>
> — Claude (Anthropic), code review, August 2026

> **WindowsGSH** feels like what you’d build if you actually ran servers for a living: one place to install and mind them, modules instead of hard-coded games, Discord and web when you want them, and docs that warn you about the sharp edges instead of pretending there aren’t any. Early days, solid bones.
>
> — Grok (xAI), August 2026
