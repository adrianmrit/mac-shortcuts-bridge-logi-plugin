using System.Text;
using System.Text.Json.Nodes;
using Loupedeck.Service;

namespace Loupedeck.MacShortcutsBridgePlugin;

internal static class DefaultActionIcon
{
    private static readonly Lazy<JsonObject> template = new(LoadTemplate);

    internal static JsonObject Template => (JsonObject)template.Value.DeepClone();

    private static JsonObject LoadTemplate()
    {
        if (!ActionIcon.TryParse(LoadDefinition(), out var icon))
        {
            throw new IOException("The default icon template could not be decoded.");
        }
        return JsonNode.Parse(JsonHelpers.SerializeAnyObject(icon))?.AsObject()
            ?? throw new IOException("The default icon template is invalid.");
    }

    private static string LoadDefinition()
    {
        var assembly = typeof(DefaultActionIcon).Assembly;
        using var template = assembly.GetManifestResourceStream(
            "Loupedeck.MacShortcutsBridgePlugin.Assets.DefaultIconTemplate.ict")
            ?? throw new IOException("The default icon template is missing.");
        using var png = assembly.GetManifestResourceStream(
            "Loupedeck.MacShortcutsBridgePlugin.Assets.Icon256x256.png")
            ?? throw new IOException("The default icon image is missing.");
        using var bytes = new MemoryStream();
        png.CopyTo(bytes);
        using var reader = new StreamReader(template, Encoding.UTF8);
        return reader.ReadToEnd().Replace("__DEFAULT_ICON_IMAGE__",
            Convert.ToBase64String(bytes.ToArray()), StringComparison.Ordinal);
    }

    internal static BitmapImage Render(int width, int height, string? name = null) =>
        Render(Template, width, height, name);

    internal static BitmapImage Render(JsonObject template, int width, int height, string? name = null)
    {
        if (!ActionIcon.TryParse(template.ToJsonString(), out var icon))
        {
            throw new IOException("The default icon template could not be decoded.");
        }
        if (!string.IsNullOrEmpty(name)) { icon.SetText(name); }
        return ActionIconBuilder.CreateImage(width, height, icon, ActionImageBuilderFlags.None)
            ?? throw new IOException("The default icon template could not be rendered.");
    }
}
