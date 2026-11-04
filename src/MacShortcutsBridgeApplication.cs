namespace Loupedeck.MacShortcutsBridgePlugin;

public sealed class MacShortcutsBridgeApplication : ClientApplication
{
    protected override string GetProcessName() => "";
    protected override string GetBundleName() => "";
    public override ClientApplicationStatus GetApplicationStatus() => ClientApplicationStatus.Unknown;
}
