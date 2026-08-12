namespace NapcatQUI.Core.Configuration;

public class AppConfig
{
    public List<AccountConfig> Accounts { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

public class AccountConfig
{
    public string Uin { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string NapCatWsUrl { get; set; } = "ws://localhost:3001";
    public string? AccessToken { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public class AppSettings
{
    public string Theme { get; set; } = "跟随系统";
    public string DbPath { get; set; } = string.Empty;
    public bool StartMinimized { get; set; }
}
