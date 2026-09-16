using System.Text;

namespace WaydroidPrefsEditor.App;

/// <summary>
/// Wire format between the GUI and the privileged helper: one line-based request, one framed reply.
/// <c>OK &lt;length&gt;\n</c> or <c>ERR &lt;length&gt;\n</c> followed by exactly that many payload bytes.
/// Framing by length keeps binary payloads (prefs XML) from colliding with the text headers.
/// </summary>
internal static class HelperProtocol
{
    public const string Ok = "OK";
    public const string Error = "ERR";

    public static void WriteResponse(Stream stream, string status, byte[] payload)
    {
        var header = Encoding.UTF8.GetBytes($"{status} {payload.Length}\n");
        stream.Write(header, 0, header.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    public static void WriteError(Stream stream, string message) =>
        WriteResponse(stream, Error, Encoding.UTF8.GetBytes(message));

    /// <summary>Reads up to (not including) the next LF. Null only at end of stream.</summary>
    public static string? ReadLine(Stream stream)
    {
        var bytes = new List<byte>(32);
        while (true)
        {
            var next = stream.ReadByte();
            if (next < 0)
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (next == '\n')
                return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add((byte)next);
        }
    }

    public static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
                throw new EndOfStreamException($"Expected {count} bytes but the stream ended after {offset}.");
            offset += read;
        }
        return buffer;
    }
}
