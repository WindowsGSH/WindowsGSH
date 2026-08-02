# Security

WindowsGSH is a Windows desktop application for managing dedicated game servers.

## Supported Versions

During early development, only the latest release is supported.

| Version | Supported |
| --- | --- |
| Latest release | Yes |
| Older releases | No |

## Reporting a Vulnerability

Please report security issues privately rather than opening a public GitHub issue.

You can contact the maintainer through the GitHub repository owner account.

When reporting a vulnerability, please include:

- The WindowsGSH version or commit.
- A clear description of the issue.
- Steps to reproduce, if possible.
- Any relevant logs or screenshots.
- Whether the issue affects local users only or could be triggered remotely.

## Release Verification

For release builds, the project aims to provide:

- GitHub Actions build results.
- Unit test results.
- SHA256 checksums for release downloads.
- Dependency scanning through Dependabot.
- VirusTotal links for public release binaries where practical.

CodeQL/code scanning may be added later when the repository is public or when GitHub settings support it.

A clean scan does not guarantee that software is completely risk-free. These checks are provided for transparency and to help users verify that the downloaded release artifact matches the published build.

## Verifying a Release Download

Each release should include a `.sha256` file.

On Windows, you can verify the hash with PowerShell:

```powershell
Get-FileHash .\WindowsGSH-v1.0.0-win-x64.zip -Algorithm SHA256
```

Compare the output with the SHA256 value shown in the GitHub Release notes or `.sha256` file.

## Code Signing

WindowsGSH release builds may not be code-signed during early development.

Unsigned Windows applications can trigger Microsoft Defender SmartScreen warnings, especially for new or low-reputation downloads. This does not automatically mean the application is malicious, but users should only download releases from the official GitHub Releases page.

Code signing may be added in a future release.

## Plugin and Module Safety

WindowsGSH supports module-based game server integrations.

Imported C# modules should be treated as trusted executable code. Only install modules from sources you trust. Manifest validation is useful, but it is not sandboxing.

Future versions may add stronger module trust controls such as signing, curation, or additional warnings.

## Sensitive Data

WindowsGSH may handle sensitive values such as:

- Steam credentials.
- Discord bot tokens.
- RCON passwords.
- Server passwords.
- API/client secrets.

Where possible, secrets should be stored using Windows user-level protection rather than plain text.

Do not share logs, configuration files, crash reports, or support bundles publicly unless you have checked them for secrets first.

## Source, forks, and unofficial builds

Source visibility does not make an unofficial build trusted. A fork can change update URLs, module loading, credential handling, network behavior, or release packaging.

- Download official builds only from locations linked by the official WindowsGSH repository or website.
- Verify release checksums where provided.
- Do not enter credentials into an unofficial or modified build unless you have reviewed and trust every relevant change.
- WindowsGSH cannot verify, support, or accept responsibility for unauthorised redistributed builds.

Private personal modifications are governed by [LICENSE.md](LICENSE.md). Branding and claims of official status are governed by [TRADEMARKS.md](TRADEMARKS.md).
