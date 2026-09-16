using System.Text;
using WaydroidPrefsEditor.Core;
using Xunit;

namespace WaydroidPrefsEditor.Tests;

public class PrefsFileTests
{
    const string Fixture = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <map>
          <int name="TutorialSteps" value="3" />
          <long name="Ticks" value="638900000000000000" />
          <float name="Volume" value="0.75" />
          <boolean name="RemoveAds" value="true" />
          <string name="PlayerId">7f3a-11ee-b9d1</string>
          <set name="Levels">
            <string>level_01</string>
            <string>level_02</string>
          </set>
        </map>
        """;

    static PrefsFile Parse(string xml) => PrefsFile.ParseXml(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    static string Write(PrefsFile file)
    {
        using var stream = new MemoryStream();
        PrefsFile.WriteXml(file, stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void ParsesEverySupportedType()
    {
        var file = Parse(Fixture);

        Assert.Collection(file.Entries,
            e => Assert.Equal(("TutorialSteps", PrefsType.Int, "3"), (e.Key, e.Type, e.Value)),
            e => Assert.Equal(("Ticks", PrefsType.Long, "638900000000000000"), (e.Key, e.Type, e.Value)),
            e => Assert.Equal(("Volume", PrefsType.Float, "0.75"), (e.Key, e.Type, e.Value)),
            e => Assert.Equal(("RemoveAds", PrefsType.Boolean, "true"), (e.Key, e.Type, e.Value)),
            e => Assert.Equal(("PlayerId", PrefsType.String, "7f3a-11ee-b9d1"), (e.Key, e.Type, e.Value)),
            e =>
            {
                Assert.Equal("Levels", e.Key);
                Assert.Equal(PrefsType.StringSet, e.Type);
                Assert.Equal(new[] { "level_01", "level_02" }, e.SetItems);
            });
    }

    [Fact]
    public void RoundTripsThroughWriteAndParse()
    {
        var original = Parse(Fixture);
        var reparsed = Parse(Write(original));

        Assert.Equal(original.Entries.Count, reparsed.Entries.Count);
        for (var i = 0; i < original.Entries.Count; i++)
        {
            Assert.Equal(original.Entries[i].Key, reparsed.Entries[i].Key);
            Assert.Equal(original.Entries[i].Type, reparsed.Entries[i].Type);
            Assert.Equal(original.Entries[i].Value, reparsed.Entries[i].Value);
            Assert.Equal(original.Entries[i].SetItems, reparsed.Entries[i].SetItems);
        }
    }

    [Fact]
    public void WritesTheExactElementText()
    {
        var file = new PrefsFile
        {
            Entries =
            {
                new PrefsEntry { Key = "x", Type = PrefsType.Int, Value = "1" },
                new PrefsEntry { Key = "n", Type = PrefsType.String, Value = "  padded  " },
                new PrefsEntry { Key = "s", Type = PrefsType.StringSet, SetItems = { "a" } },
            },
        };

        Assert.Equal(
            """
            <?xml version="1.0" encoding="utf-8" standalone="yes"?><map><int name="x" value="1" /><string name="n">  padded  </string><set name="s"><string>a</string></set></map>
            """,
            Write(file));
    }

    [Fact]
    public void EmptyMapYieldsNoEntries()
    {
        Assert.Empty(Parse("""<map />""").Entries);
        Assert.Empty(Parse("""<map></map>""").Entries);
    }

    [Fact]
    public void NonMapRootThrows()
    {
        var error = Assert.Throws<PrefsFormatException>(() => Parse("""<preferences />"""));
        Assert.Contains("preferences", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownElementThrows()
    {
        var error = Assert.Throws<PrefsFormatException>(
            () => Parse("""<map><double name="pi" value="3.14" /></map>"""));
        Assert.Contains("double", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScalarElementWithoutValueAttributeThrows()
    {
        Assert.Throws<PrefsFormatException>(() => Parse("""<map><int name="x" /></map>"""));
    }
}
