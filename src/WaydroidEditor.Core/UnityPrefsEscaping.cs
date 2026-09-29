namespace WaydroidEditor.Core;

/// <summary>
/// Unity's Android PlayerPrefs loader reads every stored key and value through
/// <c>Uri.UnescapeDataString</c>, so Unity writes them through the matching escaper. Reading the
/// file directly therefore shows <c>%7B%22quality%22%3A3%7D</c> where the game means
/// <c>{"quality":3}</c> — which also hides JSON and Base64 payloads from the classifier.
/// The editor decodes on load and re-encodes on save.
/// </summary>
public static class UnityPrefsEscaping
{
    /// <summary>
    /// Decodes only when re-encoding reproduces the stored text exactly. Anything Unity left
    /// alone therefore stays untouched, and an entry the user opens without editing is written
    /// back byte-identically — the same guarantee the MemoryPack path relies on.
    /// </summary>
    /// <param name="stored">The on-disk text of a string prefs entry.</param>
    /// <param name="decoded">The unescaped text, or <paramref name="stored"/> unchanged when it is not escaped.</param>
    /// <returns>True when the text is percent-escaped and decoded cleanly.</returns>
    public static bool TryDecode(string stored, out string decoded)
    {
        try
        {
            decoded = Uri.UnescapeDataString(stored);
            return !string.Equals(decoded, stored, StringComparison.Ordinal)
                && Uri.EscapeDataString(decoded) == stored;
        }
        catch (Exception)
        {
            // A pathological value must never abort loading the whole file.
            decoded = stored;
            return false;
        }
    }

    /// <summary>Percent-escapes editable text the way Unity's Android writer does.</summary>
    /// <param name="decoded">The unescaped text to store.</param>
    public static string Encode(string decoded) => Uri.EscapeDataString(decoded);

    /// <summary>
    /// Editable text back to stored text, using the flag <see cref="TryDecode"/> returned. Passing
    /// the flag through keeps the decision in one place, so a value Unity did not escape is
    /// written back verbatim instead of being escaped after the fact.
    /// </summary>
    /// <param name="decoded">The editable text, already unescaped.</param>
    /// <param name="wasEscaped">The flag <see cref="TryDecode"/> produced for the same value.</param>
    /// <returns>The text to store: escaped when it was escaped on disk, unchanged otherwise.</returns>
    public static string Encode(string decoded, bool wasEscaped) =>
        wasEscaped ? Encode(decoded) : decoded;

    /// <summary>The decoded form, or the stored text unchanged when it is not escaped.</summary>
    /// <param name="stored">The on-disk text of a string prefs entry.</param>
    public static string Decode(string stored) => TryDecode(stored, out var decoded) ? decoded : stored;
}
