using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PocketIris;

/// <summary>Looks up the phone's own network addresses (used by the debugging link and the network self-test).</summary>
public static class NetUtil
{
    /// <summary>The ordinary (IPv4) addresses, with their network masks, of the named connection such as wlan0 or bt-pan, only while it is up.</summary>
    /// <param name="interfaceName">The connection's name.</param>
    public static IEnumerable<UnicastIPAddressInformation> Ipv4(string interfaceName) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.Name == interfaceName && n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

    /// <summary>Every connection that is up (except the phone's loopback) with its IPv4 addresses, each as "name=address".</summary>
    public static IEnumerable<string> DescribeAll() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => $"{n.Name}={a.Address}"));
}
