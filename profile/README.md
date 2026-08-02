# WindowsGSH — Game Server Hub

**WindowsGSH** is a Windows-focused game server management platform designed to make hosting, configuring, updating, and monitoring dedicated game servers easier.

The project is inspired by the simplicity of tools like WindowsGSM, but aims to grow into a broader, more flexible hub for managing Steam, Java, modded, and standalone dedicated servers from one place.

---

## What WindowsGSH is for

WindowsGSH is being built for people who want to run game servers without manually juggling:

- SteamCMD commands
- server launch arguments
- config files
- firewall rules
- updates
- backups
- logs
- server status checks
- Discord/server notifications
- per-game quirks and custom setup steps

The goal is to provide a clean Windows desktop experience that helps server owners manage multiple dedicated servers with less hassle.

---

## Project goals

WindowsGSH aims to provide:

- A modern Windows-based server manager
- Support for multiple game server types
- Plugin/module-based game support
- Easy installation and updating of dedicated servers
- Configurable server settings per game
- Support for SteamCMD-managed servers
- Support for non-Steam and Java-based servers where possible
- Server status monitoring
- Optional Discord/community integrations
- A safer, clearer workflow for backups and updates

---

## Plugin and module approach

WindowsGSH is designed around the idea that each game can have its own module or plugin.

A module may define things like:

- Install method
- Update method
- Server executable path
- Launch arguments
- Config file locations
- Query method
- Ports
- Firewall requirements
- Backup locations
- Custom game-specific settings

This allows WindowsGSH to support many different types of dedicated servers without hardcoding every game directly into the core application.

---

## Current focus

The project is currently focused on building a solid foundation for:

- Core server management
- Game/module support
- SteamCMD integration
- Configuration handling
- Server process control
- Status monitoring
- Clean release packaging
- Community feedback and future expansion

---

## Repositories

This organization may contain repositories for:

- The main WindowsGSH application
- Game server modules/plugins
- Documentation
- Release packages
- Discord/community tooling
- Issue tracking and feature requests

Some development repositories may remain private while the project is being actively built.

---

## Community and feedback

WindowsGSH is being developed with real game server hosting use cases in mind.

Feedback, bug reports, feature requests, and game server module suggestions are welcome.

Good issue reports should include:

```text
WindowsGSH version:
Game/server module:
Operating system:
What you were trying to do:
What happened:
What you expected to happen:
Steps to reproduce:
Logs or screenshots: