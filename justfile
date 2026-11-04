default:
    @just --list

build:
    dotnet build MacShortcutsBridgePlugin.slnx -c Release

format-check:
    dotnet format MacShortcutsBridgePlugin.slnx --verify-no-changes

format:
    dotnet format MacShortcutsBridgePlugin.slnx

test:
    dotnet test MacShortcutsBridgePlugin.slnx -c Release

test-core:
    dotnet test tests/MacShortcutsBridge.Core.Tests/MacShortcutsBridge.Core.Tests.csproj -c Release
