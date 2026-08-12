using System.Text.Json.Serialization;

namespace NapcatQUI.Core.Models;

public class Account
{
    public int Id { get; set; }
    public string Uin { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string NapCatWsUrl { get; set; } = "ws://localhost:3001";
    public string? AccessToken { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset? LastConnectedAt { get; set; }

    [JsonIgnore]
    public ConnectionState State { get; set; } = ConnectionState.Disconnected;
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}
