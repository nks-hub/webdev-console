using System.Text;
using Microsoft.AspNetCore.Http;
using NKS.WebDevConsole.Daemon.Data;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// L1: the database import streams the upload to a temp file with a size
/// limit instead of reading it into a string.
/// </summary>
public sealed class DatabaseImportStreamingTests : IDisposable
{
    private readonly string _dest = Path.Combine(Path.GetTempPath(), $"wdc-import-test-{Guid.NewGuid():N}.sql");

    public void Dispose()
    {
        try { File.Delete(_dest); } catch { /* best effort */ }
    }

    private static HttpRequest RawRequest(byte[] body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = "application/sql";
        ctx.Request.Body = new MemoryStream(body);
        return ctx.Request;
    }

    private static HttpRequest MultipartRequest(byte[] file, string fileName = "dump.sql", bool withTextField = true)
    {
        const string boundary = "----wdcTestBoundary42";
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.UTF8.GetBytes(s));
        if (withTextField)
            W($"--{boundary}\r\nContent-Disposition: form-data; name=\"note\"\r\n\r\nhello\r\n");
        W($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"\r\nContent-Type: application/sql\r\n\r\n");
        ms.Write(file);
        W($"\r\n--{boundary}--\r\n");
        ms.Position = 0;

        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        ctx.Request.Body = ms;
        return ctx.Request;
    }

    [Fact]
    public async Task Raw_body_is_copied_byte_for_byte()
    {
        // Bytes that are not valid UTF-8 (a latin1 dump) survive unchanged.
        var sql = Encoding.Latin1.GetBytes("INSERT INTO t VALUES ('Příliš žluťoučký');\n");
        Assert.Null(await DatabaseEndpoints.ReceiveImportAsync(RawRequest(sql), _dest, 1024, CancellationToken.None));
        Assert.Equal(sql, await File.ReadAllBytesAsync(_dest));
    }

    [Fact]
    public async Task Multipart_file_part_is_streamed_and_other_parts_are_skipped()
    {
        var sql = Encoding.UTF8.GetBytes("CREATE TABLE t (id INT);\nINSERT INTO t VALUES (1);\n");
        Assert.Null(await DatabaseEndpoints.ReceiveImportAsync(MultipartRequest(sql), _dest, 1024, CancellationToken.None));
        Assert.Equal(sql, await File.ReadAllBytesAsync(_dest));
    }

    [Fact]
    public async Task Multipart_without_a_file_is_reported()
    {
        const string boundary = "b1";
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"x\"\r\n\r\ny\r\n--{boundary}--\r\n"));
        Assert.Equal("No file uploaded", await DatabaseEndpoints.ReceiveImportAsync(ctx.Request, _dest, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task Uploads_over_the_limit_are_refused()
    {
        var big = new byte[200_000];
        Array.Fill(big, (byte)'x');
        await Assert.ThrowsAsync<DatabaseEndpoints.ImportTooLargeException>(() =>
            DatabaseEndpoints.ReceiveImportAsync(RawRequest(big), _dest, 100_000, CancellationToken.None));
        await Assert.ThrowsAsync<DatabaseEndpoints.ImportTooLargeException>(() =>
            DatabaseEndpoints.ReceiveImportAsync(MultipartRequest(big), _dest, 100_000, CancellationToken.None));
    }

    [Fact]
    public async Task An_upload_exactly_at_the_limit_is_accepted()
    {
        var exact = new byte[100_000];
        Array.Fill(exact, (byte)'y');
        Assert.Null(await DatabaseEndpoints.ReceiveImportAsync(RawRequest(exact), _dest, 100_000, CancellationToken.None));
        Assert.Equal(100_000, new FileInfo(_dest).Length);
    }

    [Fact]
    public async Task Utf8_byte_order_mark_is_dropped()
    {
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("SELECT 'č';")).ToArray();
        Assert.Null(await DatabaseEndpoints.ReceiveImportAsync(RawRequest(body), _dest, 1024, CancellationToken.None));
        Assert.Equal(Encoding.UTF8.GetBytes("SELECT 'č';"), await File.ReadAllBytesAsync(_dest));
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    public async Task Utf16_and_utf32_dumps_are_transcoded_to_utf8(string encodingName)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var text = "INSERT INTO t VALUES ('Příliš žluťoučký kůň');\n" + new string('a', 100_000);
        var body = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        Assert.Null(await DatabaseEndpoints.ReceiveImportAsync(RawRequest(body), _dest, body.Length, CancellationToken.None));
        Assert.Equal(Encoding.UTF8.GetBytes(text), await File.ReadAllBytesAsync(_dest));
    }

    [Fact]
    public async Task Empty_body_gives_an_empty_file()
    {
        Assert.Null(await DatabaseEndpoints.ReceiveImportAsync(RawRequest([]), _dest, 1024, CancellationToken.None));
        Assert.Equal(0, new FileInfo(_dest).Length);
    }

    [Fact]
    public void Import_limit_is_512_mb()
    {
        Assert.Equal(512L * 1024 * 1024, DatabaseEndpoints.ImportMaxBytes);
    }
}
