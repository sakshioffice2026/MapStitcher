using System;
using System.Text.RegularExpressions;

namespace MapStitcher.Utilities
{
    /// <summary>
    /// Decodes AutoCAD DXF ASCII \U+XXXX unicode escape sequences (used for
    /// non-ASCII text such as Devanagari in older DXF encodings) into their
    /// real Unicode characters.
    /// </summary>
    public static class DxfUnicodeEscapeDecoder
    {
        private static readonly Regex EscapePattern =
            new Regex(@"\\U\+([0-9A-Fa-f]{4})", RegexOptions.Compiled);

        public static string Decode(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return raw ?? string.Empty;

            return EscapePattern.Replace(raw, m =>
                ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        }
    }
}