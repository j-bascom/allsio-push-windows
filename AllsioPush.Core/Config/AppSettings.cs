namespace AllsioPush.Config;

public enum ServerEnvironment
{
    Production,
    Development
}

public class AppSettings
{
    public ServerEnvironment Environment { get; set; } = ServerEnvironment.Production;
    public bool SoundEnabled { get; set; } = true;
    public bool DeferToToast { get; set; } = false;
    public bool LaunchOnStartup { get; set; } = true;

    public string ApiBase => Environment == ServerEnvironment.Production
        ? "https://sync.charlestontel.com"
        : "https://dev-sync.charlestontel.com";

    public string AdminBase => Environment == ServerEnvironment.Production
        ? "https://ai.charlestontel.com"
        : "https://dev-ai.charlestontel.com";
}
