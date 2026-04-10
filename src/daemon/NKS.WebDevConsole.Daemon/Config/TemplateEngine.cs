using Scriban;
using Scriban.Runtime;

namespace NKS.WebDevConsole.Daemon.Config;

/// <summary>
/// Thin wrapper around Scriban for rendering configuration templates.
/// </summary>
public sealed class TemplateEngine
{
    public string Render(string templateText, object model)
    {
        var template = Template.Parse(templateText);
        if (template.HasErrors)
            throw new InvalidOperationException(
                $"Template parse error: {string.Join(", ", template.Messages)}");

        var scriptObject = new ScriptObject();
        scriptObject.Import(model);
        var context = new TemplateContext();
        context.PushGlobal(scriptObject);

        return template.Render(context);
    }

    public string RenderFile(string templatePath, object model)
    {
        var templateText = File.ReadAllText(templatePath);
        return Render(templateText, model);
    }
}
