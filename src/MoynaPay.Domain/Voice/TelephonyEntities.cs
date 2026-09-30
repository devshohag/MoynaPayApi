using MoynaPay.Domain.Common;

namespace MoynaPay.Domain.Voice;

// Schema: telephony

public class SipTrunk : BaseEntity
{
    public string ProviderName { get; set; } = default!;
    public string Host { get; set; } = default!;
    public int Port { get; set; } = 5060;
    public SipAuthMode AuthMode { get; set; } = SipAuthMode.UserPassword;
    public string? Username { get; set; }
    public string? SecretStoreReference { get; set; }
    public string CallerId { get; set; } = default!;
    public bool IsActive { get; set; } = true;
}

public enum SipAuthMode
{
    UserPassword,
    IpBased,
}
