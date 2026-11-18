using System.Text;

namespace Stellar.AgyBridge.Lua;

/// <summary>Helpers to embed C# values into Lua source safely.</summary>
public static class LuaLiteral
{
    public static string Quote(string? value)
    {
        value ??= "";
        var sb = new StringBuilder(value.Length + 8);
        sb.Append('\'');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\0': break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }

    public static string Bool(bool v) => v ? "true" : "false";
}
