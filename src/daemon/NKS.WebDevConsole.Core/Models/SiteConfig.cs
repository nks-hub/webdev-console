namespace NKS.WebDevConsole.Core.Models;

public class SiteConfig
{
    public string Domain { get; set; } = "";
    public string DocumentRoot { get; set; } = "";
    public string PhpVersion { get; set; } = "8.4";
    public bool SslEnabled { get; set; }
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public string[] Aliases { get; set; } = [];
    public string? Framework { get; set; }
    public Dictionary<string, string> Environment { get; set; } = new();
}
