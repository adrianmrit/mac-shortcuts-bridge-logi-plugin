using System.Text.RegularExpressions;

namespace MacShortcutsBridge.Core;

public sealed record Shortcut(Guid Id, string Name)
{
    public string Identifier => Id.ToString("D").ToUpperInvariant();

    public static IReadOnlyList<Shortcut> ParseCatalog(string output)
    {
        var shortcuts = new List<Shortcut>();
        var identifiers = new HashSet<Guid>();
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            var match = CatalogLine.Match(line);
            if (!match.Success || !Guid.TryParseExact(match.Groups["id"].Value, "D", out var id)
                || id == Guid.Empty || !identifiers.Add(id))
            {
                throw new FormatException("Shortcuts returned an unrecognized catalog. Try reopening the list.");
            }
            shortcuts.Add(new Shortcut(id, match.Groups["name"].Value));
        }
        return shortcuts.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Id).ToArray();
    }

    public string GetChoiceLabel(IReadOnlyList<Shortcut> catalog) =>
        catalog.Count(item => string.Equals(item.Name, Name, StringComparison.CurrentCultureIgnoreCase)) > 1
            ? $"{Name} ({Identifier})"
            : Name;

    private static readonly Regex CatalogLine = new(
        @"^(?<name>.+) \((?<id>[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
