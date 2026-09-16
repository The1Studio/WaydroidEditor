using System.Text;
using System.Text.Json.Nodes;
using WaydroidPrefsEditor.Core;
using Xunit;

namespace WaydroidPrefsEditor.Tests;

/// <summary>
/// Unity's Android loader reads stored values through Uri.UnescapeDataString, so on-disk text is
/// percent-escaped. The editor decodes it for display and must re-encode it byte-identically.
/// </summary>
public class UnityPrefsEscapingTests
{
    [Theory]
    [InlineData("%7B%22quality%22%3A3%7D", """{"quality":3}""")]
    [InlineData("Player%20One%2FTwo", "Player One/Two")]
    [InlineData("%2B%2F%3D", "+/=")]
    [InlineData("CwQBEAgfFBkTAQwU0gQ%2B%2F%3D", "CwQBEAgfFBkTAQwU0gQ+/=")]
    [InlineData("caf%C3%A9", "café")]
    [InlineData("100%25", "100%")]
    public void DecodesEscapedValues(string stored, string expected)
    {
        Assert.True(UnityPrefsEscaping.TryDecode(stored, out var decoded));
        Assert.Equal(expected, decoded);
        Assert.Equal(stored, UnityPrefsEscaping.Encode(decoded));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("""{"quality":3}""")]
    [InlineData("CwQBEAgfFBkTAQwU0gQ")]
    [InlineData("a+b/c=")]
    [InlineData("50% off")]
    [InlineData("100%")]
    [InlineData("trailing%")]
    [InlineData("café")]
    public void LeavesUnescapedValuesUntouched(string stored)
    {
        Assert.False(UnityPrefsEscaping.TryDecode(stored, out var decoded));
        Assert.Equal(stored, decoded);
        Assert.Equal(stored, UnityPrefsEscaping.Decode(stored));
    }

    /// <summary>
    /// The invariant that makes an unedited entry safe to write back: escaping is applied only
    /// when it was detected, so the stored text is reproduced exactly either way.
    /// </summary>
    [Fact]
    public void WritingAnUneditedEntryBackReproducesTheStoredText()
    {
        string[] stored =
        [
            "%7B%22quality%22%3A3%2C%22vsync%22%3Afalse%7D",
            "%2B%2F%3D",
            "Player%20One%2FTwo",
            "caf%C3%A9",
            "100%25",
            "plain",
            """{"quality":3}""",
            "50% off",
            "trailing%",
            "",
        ];

        foreach (var value in stored)
        {
            // Exactly what EntryRow.ToEntry does on the way out.
            var wasEscaped = UnityPrefsEscaping.TryDecode(value, out var decoded);
            Assert.Equal(value, UnityPrefsEscaping.Encode(decoded, wasEscaped));
        }
    }

    [Fact]
    public void EscapingHidesNeitherJsonNorBase64FromTheClassifier()
    {
        var file = PrefsFile.ParseXml(new MemoryStream(Encoding.UTF8.GetBytes(
            """<map><string name="Json">%7B%22quality%22%3A3%7D</string></map>""")));

        var logical = UnityPrefsEscaping.Decode(file.Entries[0].Value);

        Assert.IsType<JsonObject>(JsonNode.Parse(logical));
        Assert.Equal([1, 2, 3], Convert.FromBase64String(UnityPrefsEscaping.Decode("AQID")));
    }

    /// <summary>
    /// The end-to-end safety property: loading an escaped file and saving it without touching a
    /// value must reproduce the file exactly, so an accidental Save cannot corrupt a game save.
    /// </summary>
    [Fact]
    public void AnUneditedEscapedFileRoundTripsByteIdentically()
    {
        const string original =
            """
            <?xml version="1.0" encoding="utf-8" standalone="yes"?><map><int name="TutorialSteps" value="3" /><string name="Graphics">%7B%22quality%22%3A3%2C%22vsync%22%3Afalse%7D</string><string name="PlayerName">Player%20One%2FTwo</string><string name="Blob">CwQBEAgfFBkTAQwU0gQ%2B%2F%3D</string><string name="Odd">100%25</string><string name="Plain">unchanged</string></map>
            """;

        var file = PrefsFile.ParseXml(new MemoryStream(Encoding.UTF8.GetBytes(original)));

        // Load (decode) then save (re-encode) every value exactly as the editor does.
        foreach (var entry in file.Entries)
        {
            if (entry.Type != PrefsType.String)
                continue;
            var wasEscaped = UnityPrefsEscaping.TryDecode(entry.Value, out var decoded);
            entry.Value = UnityPrefsEscaping.Encode(decoded, wasEscaped);
        }

        using var output = new MemoryStream();
        PrefsFile.WriteXml(file, output);

        Assert.Equal(original, Encoding.UTF8.GetString(output.ToArray()));
    }
}
