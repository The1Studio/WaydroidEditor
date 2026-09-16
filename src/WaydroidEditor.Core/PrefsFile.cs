using System.Text;
using System.Xml;

namespace WaydroidEditor.Core;

public enum PrefsType
{
    Int,
    Long,
    Float,
    Boolean,
    String,
    StringSet,
}

/// <summary>A single entry of a Unity Android PlayerPrefs file.</summary>
public sealed class PrefsEntry
{
    public string Key = "";
    public PrefsType Type;
    /// <summary>Raw text payload. For scalars this is the <c>value</c> attribute verbatim.</summary>
    public string Value = "";
    /// <summary>Element contents for <see cref="PrefsType.StringSet"/>.</summary>
    public List<string> SetItems = new();

    public PrefsEntry Clone() => new()
    {
        Key = Key,
        Type = Type,
        Value = Value,
        SetItems = new List<string>(SetItems),
    };
}

public sealed class PrefsFormatException(string message) : Exception(message);

/// <summary>
/// Reader/writer for <c>&lt;pkg&gt;.v2.playerprefs.xml</c>.
/// Uses XmlReader/XmlWriter rather than XDocument so string payload whitespace is byte-exact.
/// </summary>
public sealed class PrefsFile
{
    public List<PrefsEntry> Entries = new();

    public static PrefsFile ParseXml(Stream stream)
    {
        var file = new PrefsFile();
        var settings = new XmlReaderSettings
        {
            IgnoreWhitespace = false,
            DtdProcessing = DtdProcessing.Prohibit,
            CloseInput = false,
        };
        using var reader = XmlReader.Create(stream, settings);

        reader.MoveToContent();
        if (reader.NodeType != XmlNodeType.Element || reader.Name != "map")
            throw new PrefsFormatException($"Expected <map> root element, found <{reader.Name}>.");

        if (reader.IsEmptyElement)
        {
            reader.Read();
            return file;
        }

        reader.ReadStartElement("map");
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType == XmlNodeType.Element)
                file.Entries.Add(ReadEntry(reader));
            else if (!reader.Read())
                throw new PrefsFormatException("Unexpected end of document inside <map>.");
        }

        reader.ReadEndElement();
        return file;
    }

    static PrefsEntry ReadEntry(XmlReader reader)
    {
        var name = reader.GetAttribute("name")
            ?? throw new PrefsFormatException($"<{reader.Name}> element is missing its 'name' attribute.");

        switch (reader.Name)
        {
            case "int":
            case "long":
            case "float":
            case "boolean":
            {
                var type = reader.Name switch
                {
                    "int" => PrefsType.Int,
                    "long" => PrefsType.Long,
                    "float" => PrefsType.Float,
                    _ => PrefsType.Boolean,
                };
                var value = reader.GetAttribute("value")
                    ?? throw new PrefsFormatException($"<{reader.Name} name=\"{name}\"> is missing its 'value' attribute.");
                reader.Skip();
                return new PrefsEntry { Key = name, Type = type, Value = value };
            }
            case "string":
                return new PrefsEntry
                {
                    Key = name,
                    Type = PrefsType.String,
                    Value = reader.ReadElementContentAsString(),
                };
            case "set":
            {
                var items = new List<string>();
                if (reader.IsEmptyElement)
                {
                    reader.Read();
                }
                else
                {
                    reader.ReadStartElement("set");
                    while (!(reader.NodeType == XmlNodeType.EndElement && reader.Name == "set"))
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            if (reader.Name != "string")
                                throw new PrefsFormatException(
                                    $"Unexpected <{reader.Name}> inside <set name=\"{name}\">.");
                            items.Add(reader.ReadElementContentAsString());
                        }
                        else if (!reader.Read())
                        {
                            throw new PrefsFormatException("Unexpected end of document inside <set>.");
                        }
                    }
                    reader.ReadEndElement();
                }
                return new PrefsEntry { Key = name, Type = PrefsType.StringSet, SetItems = items };
            }
            default:
                throw new PrefsFormatException($"Unknown element <{reader.Name}> in preferences map.");
        }
    }

    public static void WriteXml(PrefsFile file, Stream stream)
    {
        var declaration = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>");
        stream.Write(declaration, 0, declaration.Length);

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            CloseOutput = false,
            NewLineHandling = NewLineHandling.None,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using var writer = XmlWriter.Create(stream, settings);

        writer.WriteStartElement("map");
        foreach (var entry in file.Entries)
        {
            var element = entry.Type switch
            {
                PrefsType.Int => "int",
                PrefsType.Long => "long",
                PrefsType.Float => "float",
                PrefsType.Boolean => "boolean",
                PrefsType.String => "string",
                _ => "set",
            };
            writer.WriteStartElement(element);
            writer.WriteAttributeString("name", entry.Key);
            switch (entry.Type)
            {
                case PrefsType.Int:
                case PrefsType.Long:
                case PrefsType.Float:
                case PrefsType.Boolean:
                    writer.WriteAttributeString("value", entry.Value);
                    break;
                case PrefsType.String:
                    writer.WriteString(entry.Value);
                    break;
                case PrefsType.StringSet:
                    foreach (var item in entry.SetItems)
                        writer.WriteElementString("string", item);
                    break;
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.Flush();
    }
}
