namespace VardyParty.LocalService.Discovery;

public interface IFirewallRuleService
{
    Task EnsureUdpDiscoveryRuleAsync(int udpPort, CancellationToken cancellationToken);
    Task RemoveUdpDiscoveryRuleAsync(int udpPort, CancellationToken cancellationToken);
}
