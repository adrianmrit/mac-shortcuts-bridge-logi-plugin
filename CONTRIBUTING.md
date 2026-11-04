# Contributing

Keep changes focused and include regression tests for behavior changes. For bug reports, include macOS, Options+, Logi Plugin Service, and plugin versions, reproduction steps, and expected versus actual behavior. Remove personal information from attached logs.

## Development

Follow the build and development-link instructions in [README.md](README.md). Commands run from the repository root.

The SDK-independent core tests require only the .NET 10 SDK:

```sh
just test-core
```

Full plugin builds and SDK tests require macOS and the locally installed Logi Plugin Service:

```sh
just build
just test
```

The plugin references the SDK in `/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle`. For another service location, pass `-p:PluginApiDir=/path/to/MonoBundle`; set `MAC_SHORTCUTS_SDK_DIR` to the same directory for SDK tests. Logitech SDK assemblies and native libraries are not bundled.

## Source layout

- `core`: Shortcuts discovery, process execution, and icon handling.
- `src`: Logitech plugin integration, command, and package assets.
- `tests`: Core and SDK contract tests.
- `src/package`: package metadata and source icon; build output goes to `bin`.

The build explicitly creates the output directories before writing the resolved default template to `metadata/DefaultIconTemplate.ict` and `icontemplates/RunShortcut.ict`. Runtime fallback rendering lazily caches the normalized template JSON and returns independent mutable copies, so rendering and edits cannot change the cached default.

Automated coverage includes discovery, execution, icon caching and rendering, saved-template synchronization, lifecycle handling, and device restrictions. Hardware, Actions Ring presentation, Automation consent, and Icon Editor behavior still require testing in Options+.

Two opt-in integration checks are excluded from normal test runs. Discovery reads the installed catalog without running a shortcut:

```sh
dotnet test tests/MacShortcutsBridge.Core.Tests -c Release --filter 'Name=DiscoverAsync_InstalledShortcuts_ReturnsValidUniqueIdentifiers'
```

The icon check executes a supplied shortcut. Use only a harmless fixture, and obtain its identifier with `shortcuts list --show-identifiers`:

```sh
MAC_SHORTCUTS_TEST_ID='YOUR-FIXTURE-UUID' dotnet test tests/MacShortcutsBridge.Core.Tests -c Release --filter 'Name=RunAndCaptureIcon_ExplicitFixture_UsesPublicMacOsInterfaces'
```

Any Automation permission requested by this test applies to the test process and may not apply to Logi Plugin Service.

To package locally, install the packaging tool if needed and run:

```sh
dotnet tool install --global LogiPluginTool
dotnet build src/MacShortcutsBridgePlugin.csproj -c Release
mkdir -p dist
DOTNET_ROLL_FORWARD=Major logiplugintool pack ./bin/Release ./dist/MacShortcutsBridge_0_1_0.lplug4
DOTNET_ROLL_FORWARD=Major logiplugintool verify ./dist/MacShortcutsBridge_0_1_0.lplug4
```

The installed tool targets .NET 8; `DOTNET_ROLL_FORWARD=Major` allows it to run with the .NET 10 runtime. The override is unnecessary if .NET 8 is installed.

## Licensing

Do not commit local settings, credentials, logs, development `.link` files, or Logitech SDK/native assemblies. The package metadata currently marks the plugin proprietary; no open-source license is granted by this repository.
