namespace TcpRouteTray;

internal readonly record struct NetworkFeaturePolicy(
    bool AllowStun = false,
    bool AllowQuic = false,
    bool EnableIpv6 = false)
{
    public IReadOnlyDictionary<string, string> EnvironmentVariables => new Dictionary<string, string>
    {
        ["TCPROUTE_ALLOW_STUN"] = AllowStun.ToString(),
        ["TCPROUTE_ALLOW_QUIC"] = AllowQuic.ToString(),
        ["TCPROUTE_ENABLE_IPV6"] = EnableIpv6.ToString()
    };
}
