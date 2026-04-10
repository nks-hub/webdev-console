using NKS.WebDevConsole.Daemon.Config;

namespace NKS.WebDevConsole.Daemon.Tests;

public class TemplateEngineTests : IDisposable
{
    private readonly TemplateEngine _engine = new();
    private readonly string _tempDir;

    public TemplateEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nks-template-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Render_SimpleTemplate_SubstitutesVariables()
    {
        var template = "Hello {{ name }}, port {{ port }}!";
        var model = new { name = "Apache", port = 8080 };

        var result = _engine.Render(template, model);

        Assert.Equal("Hello Apache, port 8080!", result);
    }

    [Fact]
    public void Render_MultipleVariables_AllSubstituted()
    {
        var template = "ServerName {{ domain }}\nDocumentRoot {{ doc_root }}\nListen {{ port }}";
        var model = new { domain = "mysite.loc", doc_root = "C:/htdocs/mysite", port = 443 };

        var result = _engine.Render(template, model);

        Assert.Contains("mysite.loc", result);
        Assert.Contains("C:/htdocs/mysite", result);
        Assert.Contains("443", result);
    }

    [Fact]
    public void Render_MissingVariable_ProducesEmpty()
    {
        var template = "Hello {{ name }}, value={{ missing_var }}!";
        var model = new { name = "Test" };

        var result = _engine.Render(template, model);

        Assert.Contains("Hello Test", result);
        // Scriban renders missing variables as empty string by default
        Assert.Contains("value=!", result);
    }

    [Fact]
    public void Render_EmptyTemplate_ReturnsEmpty()
    {
        var result = _engine.Render("", new { });

        Assert.Equal("", result);
    }

    [Fact]
    public void Render_NoVariables_ReturnsTemplateAsIs()
    {
        var template = "static content without variables";

        var result = _engine.Render(template, new { });

        Assert.Equal("static content without variables", result);
    }

    [Fact]
    public void Render_InvalidTemplate_ThrowsInvalidOperationException()
    {
        // Unclosed code block is a parse error in Scriban
        var template = "{{ if true }}open without end";

        Assert.Throws<InvalidOperationException>(() => _engine.Render(template, new { }));
    }

    [Fact]
    public void Render_ConditionalTemplate_Works()
    {
        var template = "{{ if ssl_enabled }}SSL ON{{ else }}SSL OFF{{ end }}";

        var resultOn = _engine.Render(template, new { ssl_enabled = true });
        var resultOff = _engine.Render(template, new { ssl_enabled = false });

        Assert.Equal("SSL ON", resultOn);
        Assert.Equal("SSL OFF", resultOff);
    }

    [Fact]
    public void RenderFile_ReadsAndRendersTemplate()
    {
        var templatePath = Path.Combine(_tempDir, "test.conf.tmpl");
        File.WriteAllText(templatePath, "ServerName {{ domain }}\nListen {{ port }}");

        var result = _engine.RenderFile(templatePath, new { domain = "test.loc", port = 80 });

        Assert.Contains("ServerName test.loc", result);
        Assert.Contains("Listen 80", result);
    }

    [Fact]
    public void RenderFile_NonexistentFile_ThrowsFileNotFoundException()
    {
        var fakePath = Path.Combine(_tempDir, "nonexistent.tmpl");

        Assert.ThrowsAny<Exception>(() => _engine.RenderFile(fakePath, new { }));
    }

    [Fact]
    public void Render_LoopTemplate_IteratesCollection()
    {
        var template = "{{ for alias in aliases }}{{ alias }} {{ end }}";
        var model = new { aliases = new[] { "www.test.loc", "test.loc", "api.test.loc" } };

        var result = _engine.Render(template, model);

        Assert.Contains("www.test.loc", result);
        Assert.Contains("test.loc", result);
        Assert.Contains("api.test.loc", result);
    }
}
