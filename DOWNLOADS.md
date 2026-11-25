# Downloads

Nothing is downloaded until its row says **Approved**. Everything lands inside the project's `.tools\` folder (git-ignored); delete that folder to remove it all.

| # | What | Publisher | Version | License | Source URL | Verification | SHA-256 | Status |
|---|------|-----------|---------|---------|------------|--------------|---------|--------|
| 1 | .NET SDK (zip, x64): compiler and build tools. Extracted to `.tools\dotnet`; no installer, no admin. 290 MB. | Microsoft | 10.0.404 (2026-11-10) | MIT | https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.404/dotnet-sdk-10.0.404-win-x64.zip | Matched Microsoft's published SHA-512 `330ddd2665b43cd2e428216a0d8ea1ac7bb19b5434fdc530e62010dc3b52823a620bf95489d628e1ed6f5122401793ee4887cca4a019122361a1231497f632ee` | `f94f02eb8451605a893164bbd9c14e7f00ac8914c370c322822e6ac91b106bcc` | Approved 2026-11-25, downloaded |
| 2 | `Microsoft.NETCore.App.Runtime.win-x64`: the .NET runtime bundled into the shipped app. | Microsoft | 10.0.15 | MIT | https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.15 | `dotnet nuget verify`: valid Microsoft Corporation author signature + nuget.org repository signature | `7423f58917342b9e4bf276fb164d5726d2c90fe2fe989c552b9e39056256a035` | Approved 2026-11-25, downloaded |
| 3 | `Microsoft.WindowsDesktop.App.Runtime.win-x64`: the WPF part of the runtime. | Microsoft | 10.0.15 | MIT | https://www.nuget.org/packages/Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.15 | as row 2 | `a036c174bac57a716e21dedccf96459804e4d8d8c31a08e3a8de649bf7684497` | Approved 2026-11-25, downloaded |
| 4 | `Microsoft.AspNetCore.App.Runtime.win-x64`: web-server runtime. **Not approved in advance and not used.** The SDK fetches it automatically alongside rows 2-3 during a self-contained build. None of its files are in the shipped app (checked). | Microsoft | 10.0.15 | MIT | https://www.nuget.org/packages/Microsoft.AspNetCore.App.Runtime.win-x64/10.0.15 | as row 2 | `d2a18c23269c91106c7d87695600f7fc25d56672dcdb9c59ed947f8f71611a72` | **Downloaded without prior approval; needs the user's OK or removal** |

Notes
- Microsoft publishes SHA-512 (not SHA-256) for the SDK; it was checked before extracting.
- Packages are stored in `.tools\nuget` rather than the usual per-user cache, so nothing is left outside the project.
- Metadata source: https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
