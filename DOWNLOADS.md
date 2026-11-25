# Downloads

Nothing is downloaded until its row says **Approved**. SHA-256 is recorded after download; before that, the file is checked against the publisher's own hash.

| # | What | Publisher | Version | License | Source URL | Publisher hash | SHA-256 (recorded on download) | Status |
|---|------|-----------|---------|---------|------------|----------------|-------------------------------|--------|
| 1 | .NET SDK (zip, x64) — compiler and build tools. Extracted to `.tools\dotnet` inside the project; no installer, no admin. ~290 MB. | Microsoft | 10.0.404 (released 2026-11-10) | MIT | https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.404/dotnet-sdk-10.0.404-win-x64.zip | SHA-512 `330ddd2665b43cd2e428216a0d8ea1ac7bb19b5434fdc530e62010dc3b52823a620bf95489d628e1ed6f5122401793ee4887cca4a019122361a1231497f632ee` (from Microsoft's release metadata) — verified | `f94f02eb8451605a893164bbd9c14e7f00ac8914c370c322822e6ac91b106bcc` | **Approved 2026-11-25, downloaded** |
| 2 | `Microsoft.NETCore.App.Runtime.win-x64` — the .NET runtime bundled into the shipped app so friends need nothing installed. | Microsoft | 10.0.15 | MIT | https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.15 | NuGet verifies Microsoft's package signature; content hash pinned in `packages.lock.json` | see `packages.lock.json` | **Approved 2026-11-25** |
| 3 | `Microsoft.WindowsDesktop.App.Runtime.win-x64` — the WPF part of the runtime, same purpose. | Microsoft | 10.0.15 | MIT | https://www.nuget.org/packages/Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.15 | as above | see `packages.lock.json` | **Approved 2026-11-25** |

Notes
- Microsoft publishes SHA-512 (not SHA-256) for the SDK. I verify SHA-512 before extracting and record SHA-256 here too.
- Rows 2–3 are fetched by the SDK from nuget.org during the first release build. If the build asks for any package not listed here, I stop and add it first.
- Metadata source: https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
