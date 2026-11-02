namespace Encoding.Contracts.Auth;

/// <summary>Cookie de session du central et en-tête par lequel le relais du worker transmet cette session à son agent (rattachement par compte administrateur, ADR 0034).</summary>
public static class SessionCookie
{
    public const string Name = "encoding_session";
    public const string RelayHeader = "X-Central-Session";
}
