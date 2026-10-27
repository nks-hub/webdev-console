namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// L4: the password validators and the SQL that embeds passwords are one
/// safety mechanism. The builders only double single quotes; backslashes and
/// NUL are kept out by the validators. These tests fail when a validator is
/// loosened beyond what the escaping handles, or changed at all without
/// review (the pinned rejected set).
/// </summary>
public sealed class SqlPasswordEscapingTests
{
    // Every ASCII character plus a few that matter in other charsets or look
    // like quotes / backslashes.
    public static readonly char[] Candidates =
        [.. Enumerable.Range(0, 128).Select(i => (char)i), '\u0080', ' ', 'č', '€', '’', 'ʼ', '＇', '＼', '¥'];

    private static string PasswordWith(char c) => "Ab1-" + c + "-xyz9";

    /// <summary>
    /// Reads a single-quoted SQL string literal at the start of
    /// <paramref name="text"/> the strictest way a server may: '' is a quote
    /// and a backslash escapes the next character (MySQL default, PostgreSQL
    /// with standard_conforming_strings off). Returns the decoded value and the
    /// text after the closing quote, or null when the literal does not close
    /// or contains NUL.
    /// </summary>
    private static (string Value, string After)? ReadLiteral(string text)
    {
        if (text.Length == 0 || text[0] != '\'') return null;
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0') return null;
            if (c == '\\')
            {
                if (i + 1 >= text.Length) return null;
                sb.Append(text[++i]);
                continue;
            }
            if (c == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'') { sb.Append('\''); i++; continue; }
                return (sb.ToString(), text[(i + 1)..]);
            }
            sb.Append(c);
        }
        return null;
    }

    /// <summary>Each statement that embeds the password must decode back to it exactly and end right after it.</summary>
    private static void AssertEmbeddedSafely(string sql, string marker, string password, int expectedStatements)
    {
        var statements = 0;
        var at = 0;
        while ((at = sql.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
        {
            at += marker.Length;
            var literal = ReadLiteral(sql[at..]);
            Assert.True(literal is not null, $"literal did not close for password {Escape(password)}: {sql}");
            Assert.Equal(password, literal!.Value.Value);
            Assert.StartsWith(";", literal.Value.After);
            statements++;
        }
        Assert.Equal(expectedStatements, statements);
    }

    private static string Escape(string s) => string.Concat(s.Select(c => c < 32 || c > 126 ? $"\\u{(int)c:X4}" : c.ToString()));

    [Fact]
    public void Every_password_the_mysql_validator_accepts_is_embedded_intact()
    {
        foreach (var c in Candidates)
        {
            var pw = PasswordWith(c);
            if (MySqlPasswordHelper.ValidatePassword(pw) is not null) continue;

            AssertEmbeddedSafely(MySqlPasswordHelper.BuildAlterUserSql(pw), "IDENTIFIED BY ", pw, expectedStatements: 4);
            AssertEmbeddedSafely(MySqlUserHelper.BuildCreateUserSql("app", "localhost", pw), "IDENTIFIED BY ", pw, expectedStatements: 1);
            AssertEmbeddedSafely(MySqlUserHelper.BuildAlterPasswordSql("app", "localhost", pw), "IDENTIFIED BY ", pw, expectedStatements: 1);
        }
    }

    [Fact]
    public void Every_password_the_postgresql_validator_accepts_is_embedded_intact()
    {
        foreach (var c in Candidates)
        {
            var pw = PasswordWith(c);
            if (PostgreSqlHelper.ValidatePassword(pw) is not null) continue;
            AssertEmbeddedSafely(PostgreSqlHelper.BuildAlterUserSql(pw), "WITH PASSWORD ", pw, expectedStatements: 1);
        }
    }

    // Changing what the validators reject needs a matching look at the
    // escaping above (and at the mysql/psql argument handling).
    [Fact]
    public void Validators_reject_exactly_nul_quotes_and_backslash()
    {
        char[] expected = ['\0', '"', '\'', '\\'];
        Assert.Equal(expected, Candidates.Where(c => MySqlPasswordHelper.ValidatePassword(PasswordWith(c)) is not null).ToArray());
        Assert.Equal(expected, Candidates.Where(c => PostgreSqlHelper.ValidatePassword(PasswordWith(c)) is not null).ToArray());
    }

    [Fact]
    public void The_literal_reader_catches_what_the_validators_keep_out()
    {
        // Sanity check of the reader itself: these break out of or truncate the literal.
        Assert.Null(ReadLiteral("'abc\\';"));
        Assert.Null(ReadLiteral("'ab\0c';"));
        Assert.Equal(("it's", ";"), ReadLiteral("'it''s';"));
    }
}
