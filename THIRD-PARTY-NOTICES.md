# Third-party notices

ServerAlyzer uses the following third-party software:

- **SSH.NET 2026.0.0** — Copyright the SSH.NET contributors; licensed under the MIT License. <https://github.com/sshnet/SSH.NET>
- **Bouncy Castle Cryptography 2.7.0** (transitive dependency of SSH.NET) — Copyright The Legion of the Bouncy Castle Inc.; licensed under the MIT License. <https://www.bouncycastle.org/about/license/>
- **Tmds.MDns 0.9.1** — Copyright the Tmds.MDns contributors; licensed under the MIT License. <https://github.com/tmds/Tmds.MDns>
- **WinUIEx 2.9.3** — Copyright the WinUIEx contributors; licensed under the MIT License. <https://github.com/dotMorten/WinUIEx>
- **Microsoft.Data.Sqlite 10.0.0** — Copyright the .NET Foundation and Contributors; licensed under the MIT License. <https://github.com/dotnet/efcore>
- **SQLitePCLRaw (bundle_e_sqlite3, core, provider.e_sqlite3, lib.e_sqlite3) 2.1.13** (native SQLite payload; `bundle_e_sqlite3` pinned to 2.1.13 to ship a patched `e_sqlite3`) — Copyright Eric Sink and Contributors; licensed under the Apache License 2.0. Bundles the **SQLite** database engine, which is in the public domain. <https://github.com/ericsink/SQLitePCL.raw>
- **Microsoft.Extensions.* 10.0.0** (Hosting, DependencyInjection, Logging, Logging.Debug, Configuration, and their transitive `Microsoft.Extensions.*` dependencies) — Copyright the .NET Foundation and Contributors; licensed under the MIT License. <https://github.com/dotnet/runtime>
- **Microsoft.Windows.SDK.BuildTools / Microsoft.Windows.SDK.BuildTools.MSIX** (build-time MSIX packaging tooling; not redistributed in the app) — Copyright Microsoft Corporation; licensed under the Microsoft Software License Terms. <https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools>
- **Microsoft.WindowsAppSDK 2.3.1** (Windows App SDK / WinUI 3 runtime and framework) — Copyright Microsoft Corporation; licensed under the Microsoft Software License Terms for the Windows App SDK. In the unpackaged self-contained build the runtime is redistributed with the app; in the packaged (MSIX) build it is a framework package dependency provisioned by Windows/the Microsoft Store. <https://github.com/microsoft/WindowsAppSDK>

The corresponding license texts are available from the linked upstream projects and from the NuGet packages restored during the build.
