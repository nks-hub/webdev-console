namespace NKS.WebDevConsole.Core.Models;

public enum ServiceState { Stopped, Starting, Running, Stopping, Crashed, Disabled }
public enum ServiceType { WebServer, Database, Cache, MailServer, Other }
