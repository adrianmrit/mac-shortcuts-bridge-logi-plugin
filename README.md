# Mac Shortcuts Bridge

A macOS Logi Actions SDK plugin for running saved Apple Shortcuts from MX Creative Keypad buttons and Actions Ring slots.

## Install

Open the `.lplug4` package with Logi Plugin Service to install it. In Options+, open your Keypad or Actions Ring customization and find **Mac Shortcuts Bridge** under installed plugins.

## Use

Assign **Run Shortcut** to a button or slot, choose a shortcut from the **Shortcut** list, and save. Press the control to run the selected shortcut. Reopen the list to discover shortcuts added or changed in the Shortcuts app.

## Build and develop

Install the **.NET 10 SDK**, current Options+ service, and `just`:

```sh
brew install just
```

Run commands from the repository root:

```sh
just build
just test
just test-core
```

`just test` requires the locally installed Logi Plugin Service. `just test-core` runs the SDK-independent tests.

Ordinary builds do not install or reload the plugin. To link and reload a Release build for development:

```sh
dotnet build src/MacShortcutsBridgePlugin.csproj -c Release -p:InstallDevelopmentLink=true
open -g 'loupedeck:plugin/MacShortcutsBridge/reload'
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for source layout and testing guidance.
