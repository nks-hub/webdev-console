using System.Text;
using System.Text.RegularExpressions;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// Server-side twin of the MCP server's <c>isReadOnlySql</c>
/// (services/mcp-server/src/tools/databases.ts). An MCP-token caller may run a
/// single read-only statement through /query without an intent; anything this
/// guard cannot prove read-only needs a GUI-confirmed database_query intent.
/// Keep both implementations and their test cases in step.
/// </summary>
public static partial class SqlReadOnlyGuard
{
    [GeneratedRegex(@"^(SELECT|SHOW|EXPLAIN|DESC(RIBE)?|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadOnlyVerbs();

    // SELECT/WITH/EXPLAIN/DESCRIBE bodies can still write: MySQL 8 accepts
    // WITH ... DELETE, SELECT ... INTO OUTFILE writes a file, EXPLAIN ANALYZE
    // executes. SHOW is exempt (SHOW CREATE TABLE is a read).
    [GeneratedRegex(@"^(SELECT|WITH|EXPLAIN|DESC(RIBE)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerbsNeedingBodyCheck();

    // INSERT() and REPLACE() are also string functions, so they only count
    // when no "(" follows.
    [GeneratedRegex(@"\b(?:(?:INSERT|REPLACE)\b(?!\s*\()|(?:UPDATE|DELETE|MERGE|CREATE|DROP|ALTER|TRUNCATE|RENAME|GRANT|REVOKE|CALL|LOAD|HANDLER|OUTFILE|DUMPFILE)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteKeywords();

    // MySQL runs /*! ... */ and MariaDB /*M! ... */ bodies.
    [GeneratedRegex(@"/\*M?!", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecutableComment();

    public static bool IsReadOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return false;
        if (ExecutableComment().IsMatch(sql)) return false;
        var normalized = StripLeadingCommentsAndWhitespace(sql);
        if (!ReadOnlyVerbs().IsMatch(normalized)) return false;
        if (HasInnerSemicolon(sql)) return false;
        if (VerbsNeedingBodyCheck().IsMatch(normalized) && WriteKeywords().IsMatch(StripLiteralsAndComments(normalized)))
            return false;
        return true;
    }

    private static string StripLeadingCommentsAndWhitespace(string sql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            var ch = sql[i];
            if (ch is ' ' or '\t' or '\r' or '\n') { i++; continue; }
            if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return sql[i..];
                i = end + 2;
                continue;
            }
            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var nl = sql.IndexOf('\n', i + 2);
                if (nl < 0) return string.Empty;
                i = nl + 1;
                continue;
            }
            break;
        }
        return sql[i..];
    }

    private static bool HasInnerSemicolon(string sql)
    {
        var trimmed = sql.TrimEnd().TrimEnd(';').TrimEnd();
        while (trimmed.EndsWith(';')) trimmed = trimmed.TrimEnd(';').TrimEnd();
        return trimmed.Contains(';');
    }

    private static string StripLiteralsAndComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var ch = sql[i];
            if (ch is '\'' or '"' or '`')
            {
                var j = i + 1;
                while (j < sql.Length)
                {
                    // Backslash escapes exist only in string literals; identifiers
                    // escape a backtick by doubling it.
                    if (ch != '`' && sql[j] == '\\') { j += 2; continue; }
                    if (sql[j] == ch)
                    {
                        if (j + 1 < sql.Length && sql[j + 1] == ch) { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                sb.Append(ch).Append(ch);
                i = j + 1;
                continue;
            }
            if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                sb.Append(' ');
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }
            // MySQL starts a -- comment only when whitespace follows.
            var dashComment = ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-'
                && (i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2]));
            if (dashComment || ch == '#')
            {
                var nl = sql.IndexOf('\n', i);
                sb.Append(' ');
                i = nl < 0 ? sql.Length : nl;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }
}
