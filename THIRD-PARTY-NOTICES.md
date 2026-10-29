# Third-party components

## sing-box 1.14.4

Copyright (C) 2022 by nekohasekai and contributors.
License: GNU GPL version 3 or later, with the upstream additional name/association restriction reproduced in `engine/LICENSE.sing-box`.

- Project: https://github.com/SagerNet/sing-box
- Exact release and binary download: https://github.com/SagerNet/sing-box/releases/tag/v1.14.4
- Corresponding upstream source: https://github.com/SagerNet/sing-box/tree/v1.14.4
- Source archive: https://github.com/SagerNet/sing-box/archive/refs/tags/v1.14.4.tar.gz
- The release archive and executable SHA256 values are recorded in `engine/engine.lock.json`.

The engine is distributed unmodified as a separate executable. NICRoute is an independent management UI and is not endorsed by or affiliated with SagerNet. The original engine license notice and a full GPLv3 text accompany the distribution. Consult the exact source's `go.mod` and `go.sum` for its transitive dependencies and their licenses. Before redistributing the engine commercially or on another download service, make its complete corresponding source available in accordance with its license.

## .NET / Windows Desktop runtime (self-contained package)

Copyright (c) .NET Foundation and Contributors; Microsoft Corporation.
Runtime: 10.0.14 for Windows x64, verified against Microsoft's official release metadata during this build.

The runtime package's original license and third-party notices are copied to the self-contained publish output under `licenses/` (`NET-LICENSE.txt`, `NET-THIRD-PARTY-NOTICES.txt`, `WindowsDesktop-LICENSE.txt`). Sources and license information:

- https://github.com/dotnet/runtime
- https://github.com/dotnet/wpf
- https://github.com/dotnet/winforms
- https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json

NICRoute itself uses only .NET framework libraries and Windows system APIs; no additional NuGet packages are required.

## MUDL-C 0.5.14

Copyright (C) 2026 daxiaamu. License: GPL-3.0-only.

The unmodified Windows x64 executable is bundled as `engine/mudl.exe`. Its original license is `engine/LICENSE.mudl`, and its version, source URL and SHA256 are in `engine/mudl.lock.json`. It is independently hash-verified before use.

- Project: https://github.com/daxiaamu/mudl-c
- Exact release: https://github.com/daxiaamu/mudl-c/releases/tag/v0.5.14
- Corresponding source: https://github.com/daxiaamu/mudl-c/archive/refs/tags/v0.5.14.tar.gz

MUDL handles concurrent mirrors and ranges; NICRoute independently verifies the final update ZIP before staging and installation. The download engine is a separate executable, not an additional NuGet dependency.
