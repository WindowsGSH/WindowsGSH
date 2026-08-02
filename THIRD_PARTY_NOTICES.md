# WindowsGSH third-party notices

WindowsGSH includes third-party software distributed under the licences listed below. These components remain the property of their respective copyright holders. Their inclusion does not imply endorsement of WindowsGSH.

This inventory covers production dependencies published with WindowsGSH. Test-only dependencies are not included in release archives. Versions reflect the repository dependency audit performed on 2026-08-01 and must be updated when package versions change.

## Dependency inventory

| Component | Version | Licence | Project |
| --- | ---: | --- | --- |
| `WPF-UI`, `WPF-UI.Abstractions` | 4.3.0 | MIT, with embedded notices below | <https://github.com/lepoco/wpfui> |
| `Discord.Net` package family | 3.20.1 | MIT | <https://github.com/discord-net/Discord.Net> |
| `Microsoft.CodeAnalysis.Common`, `Microsoft.CodeAnalysis.CSharp` | 5.6.0 | MIT | <https://github.com/dotnet/roslyn> |
| `Microsoft.Data.Sqlite.Core` | 10.0.10 | MIT | <https://github.com/dotnet/dotnet> |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.0 | MIT | <https://github.com/dotnet/dotnet> |
| `System.Security.Cryptography.ProtectedData` | 10.0.10 | MIT | <https://github.com/dotnet/dotnet> |
| `Newtonsoft.Json` | 13.0.4 | MIT | <https://github.com/JamesNK/Newtonsoft.Json> |
| `OpenGSQ` | 2.1.5 | MIT | <https://github.com/opengsq/opengsq-dotnet> |
| `SharpZipLib` | 1.4.2 | MIT | <https://github.com/icsharpcode/SharpZipLib> |
| `SQLitePCLRaw.config.e_sqlite3`, `SQLitePCLRaw.core`, `SQLitePCLRaw.provider.e_sqlite3` | 3.0.4 | Apache-2.0 | <https://github.com/ericsink/SQLitePCL.raw> |
| `SourceGear.sqlite3` / SQLite | 3.53.3 | Public domain | <https://github.com/sourcegear/sqlite-builds> |

The `Discord.Net` family includes its Commands, Core, Dave, Interactions, Rest, Webhook, and WebSocket assemblies. Package-family entries are grouped because they are released together under the same project licence.

## MIT-licensed copyright notices

- WPF UI: Copyright (c) 2021-2025 Leszek Pomianowski and WPF UI Contributors.
- Discord.Net: Copyright (c) 2015-2024 Discord.Net Contributors.
- Microsoft .NET and Roslyn components: Copyright (c) .NET Foundation and Contributors.
- Newtonsoft.Json: Copyright (c) 2007 James Newton-King.
- OpenGSQ: Copyright (c) 2021 OpenGSQ.
- SharpZipLib: Copyright (c) 2000-2022 SharpZipLib Contributors.

### MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## WPF UI embedded third-party notices

WPF UI 4.3.0 reports that it incorporates these components:

1. VirtualizingWrapPanel 2.0.6 — <https://github.com/sbaeumlisberger/VirtualizingWrapPanel>
2. Fluent UI System Icons 1.1.242 — <https://github.com/microsoft/fluentui-system-icons>
3. .NET WPF 8.0 — <https://github.com/dotnet/wpf>
4. Microsoft UI XAML 3.0 — <https://github.com/microsoft/microsoft-ui-xaml>
5. Segoe Fluent Icons Font 3.0 — <https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font>

Copyright notices for the MIT-licensed embedded components:

- VirtualizingWrapPanel: Copyright (c) 2019 S. Bäumlisberger.
- Fluent UI System Icons: Copyright (c) 2020 Microsoft Corporation.
- .NET WPF: Copyright (c) .NET Foundation and Contributors.
- Microsoft UI XAML: Copyright (c) Microsoft Corporation. All rights reserved.

Those four components are covered by the MIT License reproduced above.

The WPF UI notice supplies the following separate terms for Segoe Fluent Icons Font:

> You may use the Segoe and icon fonts, or glyphs included in this file (“Software”) solely to design, develop and test your programs that run on a Microsoft Platform. A Microsoft Platform includes, but is not limited to, hardware or software products or services identified as Microsoft products or services. This licence does not grant the right to distribute or sublicense all or part of the Software to a third party. By using the Software, you agree to these terms. If you do not agree, do not use the Software.

## SQLite public-domain notice

SQLite is in the public domain. See <https://sqlite.org/copyright.html>.

## Apache License 2.0

SQLitePCLRaw 3.0.4 is licensed under the Apache License, Version 2.0.

Copyright 2014-2025 SourceGear, LLC

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

<https://www.apache.org/licenses/LICENSE-2.0>

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

The complete Apache License 2.0 text is available from the canonical URL above.

## Distribution scope

WindowsGSH's own source-available licence does not replace, restrict, or relicense these third-party components. Each component remains available under its stated terms. Consult the linked upstream project and the exact NuGet package for additional notices that may apply to a particular version.
