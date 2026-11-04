using MacShortcutsBridge.Core;

namespace MacShortcutsBridge.Core.Tests;

[TestFixture]
public sealed class ShortcutTests
{
    private const string Id = "247018B4-F154-452C-B0E8-09F0E8D314C3";

    [TestCase("Coffee")]
    [TestCase("Résumé ☕ (work)")]
    [TestCase("do \"things\"; $(not a command)")]
    [TestCase("Leading and trailing  ")]
    public void ParseCatalog_ValidNames_PreservesEntireName(string name)
    {
        var catalog = Shortcut.ParseCatalog($"{name} ({Id})\r\n");
        Assert.That(catalog.Single(), Is.EqualTo(new Shortcut(Guid.Parse(Id), name)));
    }

    [TestCase("")]
    [TestCase("\n\r\n  \n")]
    public void ParseCatalog_EmptyOutput_ReturnsNoShortcuts(string output) =>
        Assert.That(Shortcut.ParseCatalog(output), Is.Empty);

    [TestCase("Unexpected output")]
    [TestCase("Name (not-an-id)")]
    [TestCase("Name (00000000-0000-0000-0000-000000000000)")]
    [TestCase(" (247018B4-F154-452C-B0E8-09F0E8D314C3)")]
    public void ParseCatalog_InvalidOutput_ReportsFormatError(string output) =>
        Assert.Throws<FormatException>(() => Shortcut.ParseCatalog(output));

    [Test]
    public void ParseCatalog_RepeatedIdentifier_ReportsFormatError() =>
        Assert.Throws<FormatException>(() => Shortcut.ParseCatalog($"One ({Id})\nTwo ({Id})"));

    [Test]
    public void GetChoiceLabel_DuplicateNames_IncludesFullIdentifier()
    {
        var first = new Shortcut(Guid.Parse(Id), "Same name");
        var second = new Shortcut(Guid.NewGuid(), "Same name");
        Assert.That(first.GetChoiceLabel([first, second]), Is.EqualTo($"Same name ({Id})"));
    }

    [Test]
    public void ParseCatalog_SeveralNames_SortsForDisplay()
    {
        var result = Shortcut.ParseCatalog($"Zebra ({Id})\nApple ({Guid.NewGuid():D})");
        Assert.That(result.Select(item => item.Name), Is.EqualTo(new[] { "Apple", "Zebra" }));
    }
}
