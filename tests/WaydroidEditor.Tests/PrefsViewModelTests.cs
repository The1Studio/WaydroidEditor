using WaydroidEditor;
using WaydroidEditor.Core;
using Xunit;

namespace WaydroidEditor.Tests;

/// <summary>Exercises the window's view model against a temp package and an isolated settings file.</summary>
public sealed class PrefsViewModelTests : IDisposable
{
    const string FixtureXml =
        "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>"
        + "<map><int name=\"Gems\" value=\"5\" /><string name=\"Name\">Hero</string>"
        + "<string name=\"Escaped\">100%2525</string></map>";

    // LoadPackage calls AppSettings.Save(). Point the settings file at a throwaway config home so
    // the developer's own settings are never rewritten; the backup is the fallback when the
    // override does not take effect. The static constructor runs before the first AppSettings use.
    static readonly string ConfigRoot =
        Path.Combine(Path.GetTempPath(), "wpe-config-" + Guid.NewGuid().ToString("N"));
    static readonly bool ConfigIsolated;

    static PrefsViewModelTests()
    {
        Directory.CreateDirectory(ConfigRoot);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", ConfigRoot);
        ConfigIsolated = AppSettings.FilePath.StartsWith(ConfigRoot, StringComparison.Ordinal);
    }

    readonly string _root = Path.Combine(Path.GetTempPath(), "wpe-prefs-" + Guid.NewGuid().ToString("N"));
    readonly string _prefsPath;
    readonly PrefsViewModel _vm;

    readonly string? _realSettingsPath;
    readonly byte[]? _realSettings;

    /// <summary>Writes a fixture package and selects it under an isolated config home.</summary>
    public PrefsViewModelTests()
    {
        _prefsPath = Path.Combine(_root, "data", "com.test", "shared_prefs", "com.test.v2.playerprefs.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(_prefsPath)!);
        File.WriteAllText(_prefsPath, FixtureXml);

        if (!ConfigIsolated)
        {
            _realSettingsPath = AppSettings.FilePath;
            _realSettings = File.Exists(_realSettingsPath) ? File.ReadAllBytes(_realSettingsPath) : null;
        }

        _vm = new PrefsViewModel(new StartupOptions { DataRoot = _root, NoElevate = true });
        _vm.SelectedPackage = "com.test";
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        if (Directory.Exists(ConfigRoot))
            Directory.Delete(ConfigRoot, recursive: true);

        if (_realSettingsPath is null)
            return;
        if (_realSettings is null)
            File.Delete(_realSettingsPath);
        else
            File.WriteAllBytes(_realSettingsPath, _realSettings);
    }

    /// <summary>Removing a middle row leaves the next one selected.</summary>
    [Fact]
    public void RemoveRowDropsOneRowAndSelectsTheNeighbour()
    {
        _vm.SelectedRow = _vm.Rows[1];

        _vm.RemoveRowCommand.Execute(null);

        Assert.Equal(new[] { "Gems", "Escaped" }, _vm.Rows.Select(r => r.Key));
        Assert.Same(_vm.Rows[1], _vm.SelectedRow);
    }

    /// <summary>Removing the last row falls back to the new last row.</summary>
    [Fact]
    public void RemovingTheLastRowSelectsTheNewLastRow()
    {
        _vm.SelectedRow = _vm.Rows[2];

        _vm.RemoveRowCommand.Execute(null);

        Assert.Equal(2, _vm.Rows.Count);
        Assert.Same(_vm.Rows[1], _vm.SelectedRow);
    }

    /// <summary>Removing everything clears the rows and the selection.</summary>
    [Fact]
    public void RemoveAllRowsEmptiesTheGrid()
    {
        _vm.RemoveAllRowsCommand.Execute(null);

        Assert.Empty(_vm.Rows);
        Assert.Null(_vm.SelectedRow);
        Assert.False(_vm.HasRows);
        Assert.False(_vm.HasSelectedRow);
    }

    /// <summary>The selection flag tracks the selected row.</summary>
    [Fact]
    public void HasSelectedRowFollowsTheSelection()
    {
        Assert.True(_vm.HasSelectedRow);

        _vm.SelectedRow = null;

        Assert.False(_vm.HasSelectedRow);
    }

    /// <summary>Saving after a removal omits that key and backs up the file.</summary>
    [Fact]
    public void SaveDropsTheRemovedKey()
    {
        _vm.SelectedRow = _vm.Rows.Single(r => r.Key == "Name");
        _vm.RemoveRowCommand.Execute(null);

        _vm.SaveCommand.Execute(null);

        var saved = PrefsFile.ParseXml(new MemoryStream(File.ReadAllBytes(_prefsPath)));
        Assert.Equal(new[] { "Gems", "Escaped" }, saved.Entries.Select(e => e.Key));
        Assert.True(File.Exists(_prefsPath + ".wpe.bak"));
    }

    /// <summary>Saving an emptied grid writes an empty map.</summary>
    [Fact]
    public void SaveAfterRemoveAllWritesAnEmptyMap()
    {
        _vm.RemoveAllRowsCommand.Execute(null);

        _vm.SaveCommand.Execute(null);

        Assert.Equal(
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?><map />",
            File.ReadAllText(_prefsPath));
    }

    /// <summary>Saving without edits reproduces the stored values exactly.</summary>
    [Fact]
    public void SaveLeavesUntouchedKeysOnDiskUnchanged()
    {
        _vm.SaveCommand.Execute(null);

        var saved = PrefsFile.ParseXml(new MemoryStream(File.ReadAllBytes(_prefsPath)));
        Assert.Equal("100%2525", saved.Entries.Single(e => e.Key == "Escaped").Value);
        Assert.Equal("5", saved.Entries.Single(e => e.Key == "Gems").Value);
    }

    /// <summary>An import restores a key that had been removed, as a new row.</summary>
    [Fact]
    public void ApplyImportedRecreatesARemovedKey()
    {
        _vm.SelectedRow = _vm.Rows.Single(r => r.Key == "Name");
        _vm.RemoveRowCommand.Execute(null);

        var applied = _vm.ApplyImported(
            [new PrefsEntry { Key = "Name", Type = PrefsType.String, Value = "Hero" }]);

        Assert.Equal(1, applied);
        Assert.Equal(3, _vm.Rows.Count);
        var row = _vm.Rows.Single(r => r.Key == "Name");
        Assert.Equal(EditMode.Raw, row.Mode);
        Assert.Equal("Hero", row.RawValue);
    }

    /// <summary>An import into an empty grid leaves a row selected.</summary>
    [Fact]
    public void ApplyImportedSelectsARowOnAWipedGrid()
    {
        _vm.RemoveAllRowsCommand.Execute(null);

        _vm.ApplyImported([new PrefsEntry { Key = "Name", Type = PrefsType.String, Value = "Hero" }]);

        Assert.Same(_vm.Rows[0], _vm.SelectedRow);
    }

    /// <summary>An escaped value round-trips through import and save without being decoded twice.</summary>
    [Fact]
    public void ApplyImportedRestoresAnEscapedValueWithoutDoubleDecoding()
    {
        _vm.SelectedRow = _vm.Rows.Single(r => r.Key == "Escaped");
        _vm.RemoveRowCommand.Execute(null);

        _vm.ApplyImported(
            [new PrefsEntry { Key = "Escaped", Type = PrefsType.String, Value = "100%2525" }]);

        Assert.Equal("100%25", _vm.Rows.Single(r => r.Key == "Escaped").RawValue);

        _vm.SaveCommand.Execute(null);
        var saved = PrefsFile.ParseXml(new MemoryStream(File.ReadAllBytes(_prefsPath)));
        Assert.Equal("100%2525", saved.Entries.Single(e => e.Key == "Escaped").Value);
    }

    /// <summary>Importing keys that are still present updates them in place.</summary>
    [Fact]
    public void ApplyImportedDoesNotDuplicateAKeyThatStillExists()
    {
        var applied = _vm.ApplyImported(
        [
            new PrefsEntry { Key = "Gems", Type = PrefsType.Int, Value = "5" },
            new PrefsEntry { Key = "Name", Type = PrefsType.String, Value = "Hero" },
            new PrefsEntry { Key = "Escaped", Type = PrefsType.String, Value = "100%2525" },
        ]);

        Assert.Equal(3, applied);
        Assert.Equal(new[] { "Gems", "Name", "Escaped" }, _vm.Rows.Select(r => r.Key));
    }

    /// <summary>A JSON row whose key resolves to a type gets the field editor.</summary>
    [Fact]
    public void JsonRowWithAResolvedTypeGetsTheStructuredEditor()
    {
        WritePackage("com.json", "FakePlayerData", """{"Gold":42}""");

        // Order matters: ReloadRuntime reloads whatever package is selected, so select first.
        _vm.SelectedPackage = "com.json";
        _vm.GameDlls.Add(Path.Combine(AppContext.BaseDirectory, "FakeGameData.dll"));
        _vm.ReloadRuntime();

        var row = _vm.Rows.Single(r => r.Key == "FakePlayerData");
        Assert.Equal(EditMode.Json, row.Mode);
        Assert.Equal("JSON", row.TypeLabel);
        Assert.NotNull(row.Editor);
        Assert.Equal("FakePlayerData", row.SchemaType!.Name);
        Assert.True(row.IsStructuredMode);
    }

    /// <summary>A JSON row whose key resolves to nothing keeps the text pane.</summary>
    [Fact]
    public void JsonRowWithoutAResolvedTypeKeepsTheTextEditor()
    {
        WritePackage("com.json", "NotAType", """{"a":1}""");

        _vm.SelectedPackage = "com.json";
        _vm.GameDlls.Add(Path.Combine(AppContext.BaseDirectory, "FakeGameData.dll"));
        _vm.ReloadRuntime();

        var row = _vm.Rows.Single(r => r.Key == "NotAType");
        Assert.Equal(EditMode.Json, row.Mode);
        Assert.Null(row.Editor);
        Assert.True(row.IsTextMode);
    }

    void WritePackage(string package, string key, string value)
    {
        var path = Path.Combine(_root, "data", package, "shared_prefs", package + ".v2.playerprefs.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>"
            + $"<map><string name=\"{key}\">{value}</string></map>");
    }
}
