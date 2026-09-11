// LuaTableParser.cs
// Parses the specific, limited subset of Lua table syntax that WoW's
// SavedVariables serializer produces:
//   Name = {
//       ["key"] = "string value",
//       ["key2"] = 123.45,
//       ["key3"] = true,
//       ["nested"] = { ["key"] = ... },
//   }
// Not a general Lua parser - just enough to round-trip our own addon's
// output.

using System.Text;

namespace GuildPUGFinderApp;

public static class LuaTableParser
{
    // Parses "VarName = { ... }" and returns (varName, table).
    public static (string VarName, Dictionary<string, object?> Table) ParseAssignment(string luaText)
    {
        int eq = luaText.IndexOf('=');
        if (eq < 0) throw new FormatException("No top-level assignment found.");

        string varName = luaText[..eq].Trim();
        var pos = eq + 1;
        SkipWhitespace(luaText, ref pos);

        var value = ParseValue(luaText, ref pos);
        if (value is not Dictionary<string, object?> table)
            throw new FormatException("Top-level value is not a table.");

        return (varName, table);
    }

    private static object? ParseValue(string s, ref int pos)
    {
        SkipWhitespace(s, ref pos);
        char c = s[pos];

        if (c == '{') return ParseTable(s, ref pos);
        if (c == '"') return ParseString(s, ref pos);
        if (char.IsDigit(c) || c == '-') return ParseNumber(s, ref pos);
        if (s[pos..].StartsWith("true")) { pos += 4; return true; }
        if (s[pos..].StartsWith("false")) { pos += 5; return false; }
        if (s[pos..].StartsWith("nil")) { pos += 3; return null; }

        throw new FormatException($"Unexpected character '{c}' at position {pos}.");
    }

    private static Dictionary<string, object?> ParseTable(string s, ref int pos)
    {
        var result = new Dictionary<string, object?>();
        pos++; // consume '{'
        int autoIndex = 1;

        while (true)
        {
            SkipWhitespace(s, ref pos);
            if (s[pos] == '}') { pos++; break; }

            string key;
            if (s[pos] == '[')
            {
                pos++; // consume '['
                SkipWhitespace(s, ref pos);
                if (s[pos] == '"')
                {
                    key = ParseString(s, ref pos);
                }
                else
                {
                    // numeric key, e.g. [1] = ...
                    int start = pos;
                    while (s[pos] != ']') pos++;
                    key = s[start..pos].Trim();
                }
                SkipWhitespace(s, ref pos);
                if (s[pos] != ']') throw new FormatException($"Expected ']' at {pos}.");
                pos++; // consume ']'
                SkipWhitespace(s, ref pos);
                if (s[pos] != '=') throw new FormatException($"Expected '=' at {pos}.");
                pos++; // consume '='
            }
            else
            {
                // bare positional entry (array-style), no explicit key
                key = (autoIndex++).ToString();
            }

            var value = ParseValue(s, ref pos);
            result[key] = value;

            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ',') { pos++; continue; }
            if (pos < s.Length && s[pos] == '}') { pos++; break; }
        }

        return result;
    }

    private static string ParseString(string s, ref int pos)
    {
        pos++; // consume opening quote
        var sb = new StringBuilder();
        while (s[pos] != '"')
        {
            if (s[pos] == '\\' && pos + 1 < s.Length)
            {
                pos++;
                sb.Append(s[pos] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => s[pos]
                });
            }
            else
            {
                sb.Append(s[pos]);
            }
            pos++;
        }
        pos++; // consume closing quote
        return sb.ToString();
    }

    private static double ParseNumber(string s, ref int pos)
    {
        int start = pos;
        if (s[pos] == '-') pos++;
        while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.')) pos++;
        return double.Parse(s[start..pos], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void SkipWhitespace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    // Serializes a table back to the same Lua syntax, for writing results
    // back into the SavedVariables file.
    public static string Serialize(string varName, Dictionary<string, object?> table)
    {
        var sb = new StringBuilder();
        sb.Append(varName).Append(" = ");
        SerializeTable(sb, table, 0);
        return sb.ToString();
    }

    private static void SerializeTable(StringBuilder sb, Dictionary<string, object?> table, int indent)
    {
        sb.Append("{\n");
        string pad = new string(' ', (indent + 1) * 4);
        foreach (var (key, value) in table)
        {
            sb.Append(pad).Append('[').Append('"').Append(key).Append('"').Append("] = ");
            SerializeValue(sb, value, indent + 1);
            sb.Append(",\n");
        }
        sb.Append(new string(' ', indent * 4)).Append('}');
    }

    private static void SerializeValue(StringBuilder sb, object? value, int indent)
    {
        switch (value)
        {
            case null:
                sb.Append("nil");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case double d:
                sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case string str:
                sb.Append('"').Append(str.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                break;
            case Dictionary<string, object?> nested:
                SerializeTable(sb, nested, indent);
                break;
            default:
                sb.Append('"').Append(value.ToString()).Append('"');
                break;
        }
    }
}