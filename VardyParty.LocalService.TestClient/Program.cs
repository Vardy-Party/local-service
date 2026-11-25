using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VardyParty.LocalService.Client.Discovery;

// Test client to verify LAN discovery and HTTP connectivity to the service
// Simulates what Android TV/Phone does

Console.WriteLine("=== VardyParty Local Service Test Client ===\n");

var testResults = new List<string>();

// 1. UDP Discovery Test
Console.WriteLine("[1/3] Testing UDP Discovery...");
try
{
    var discoveryPorts = DiscoveryProtocol.DiscoveryPorts.Where(p => p > 0).Distinct().ToList();
    var probePayload = JsonSerializer.Serialize(new
    {
        type = DiscoveryProtocol.ProbeType,
        version = DiscoveryProtocol.Version
    });
    var probeBytes = Encoding.UTF8.GetBytes(probePayload);

    using var udpClient = new UdpClient();
    udpClient.EnableBroadcast = true;
    udpClient.Client.ReceiveTimeout = 5000;

    var broadcastAddress = IPAddress.Broadcast;
    var discoveryFound = false;
    var responseData = string.Empty;

    foreach (var port in discoveryPorts)
    {
        try
        {
            Console.WriteLine($"   Probing UDP port {port}...");
            await udpClient.SendAsync(probeBytes, probeBytes.Length, new IPEndPoint(broadcastAddress, port));

            var result = await udpClient.ReceiveAsync();
            responseData = Encoding.UTF8.GetString(result.Buffer);

            if (responseData.Contains(DiscoveryProtocol.ResponseType))
            {
                Console.WriteLine($"   ✓ Discovery response received on port {port}");
                Console.WriteLine($"   Response: {responseData}\n");
                discoveryFound = true;
                testResults.Add("✓ UDP Discovery: SUCCESS");
                break;
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            Console.WriteLine($"   ✗ No response on port {port} (timeout)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ✗ Error on port {port}: {ex.Message}");
        }
    }

    if (!discoveryFound)
    {
        Console.WriteLine("   ✗ No UDP discovery responses received\n");
        testResults.Add("✗ UDP Discovery: FAILED - Firewall may be blocking UDP ports");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"   ✗ UDP Discovery error: {ex.Message}\n");
    testResults.Add($"✗ UDP Discovery: ERROR - {ex.Message}");
}

// 2. mDNS Discovery Test (if avahi-sharp or mdns lib available - simplified check)
Console.WriteLine("[2/3] Testing mDNS Discovery...");
Console.WriteLine($"   Checking for mDNS service type: {DiscoveryProtocol.ServiceType}");
Console.WriteLine("   (mDNS resolution requires special libraries - see manual test)");
testResults.Add("~ mDNS Discovery: REQUIRES bonjour/avahi library");

// 3. Direct HTTP Connection Test
Console.WriteLine("\n[3/3] Testing Direct HTTP Connection...");
var httpPorts = DiscoveryProtocol.HttpPorts.Where(p => p > 0).Distinct().ToList();

// Try to find local IPs
var hostName = Dns.GetHostName();
var ipHostEntry = Dns.GetHostEntry(hostName);
var localIps = ipHostEntry.AddressList
    .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !ip.Equals(IPAddress.Loopback))
    .ToList();

Console.WriteLine($"   Local machine: {hostName}");
Console.WriteLine($"   Local IPv4 addresses:");
foreach (var ip in localIps)
{
    Console.WriteLine($"     - {ip}");
}

var httpConnected = false;
foreach (var ip in localIps)
{
    foreach (var port in httpPorts)
    {
        try
        {
            Console.WriteLine($"\n   Trying http://{ip}:{port}/health...");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync($"http://{ip}:{port}/health");

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"   ✓ HTTP connection successful!");
                Console.WriteLine($"   Status: {response.StatusCode}");
                Console.WriteLine($"   Response: {content}\n");
                testResults.Add($"✓ HTTP Connection: SUCCESS - {ip}:{port}");
                httpConnected = true;
                break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ✗ Connection failed: {ex.Message}");
        }
    }
    if (httpConnected) break;
}

if (!httpConnected)
{
    Console.WriteLine("\n   ✗ Could not connect to service via HTTP");
    testResults.Add("✗ HTTP Connection: FAILED - Firewall may be blocking TCP ports or service not running");
}

// Summary
Console.WriteLine("\n" + new string('=', 60));
Console.WriteLine("TEST SUMMARY");
Console.WriteLine(new string('=', 60));
foreach (var result in testResults)
{
    Console.WriteLine(result);
}

var successCount = testResults.Count(r => r.StartsWith("✓"));
var failCount = testResults.Count(r => r.StartsWith("✗"));

Console.WriteLine($"\nResults: {successCount} passed, {failCount} failed");

if (failCount > 0)
{
    Console.WriteLine("\n⚠️  TROUBLESHOOTING:");
    Console.WriteLine("1. Verify firewall rules are created:");
    Console.WriteLine("   netsh advfirewall firewall show rule name=\"VardyParty*\"");
    Console.WriteLine("\n2. Check if service is running:");
    Console.WriteLine("   Get-Service VardyPartyLocalService");
    Console.WriteLine("\n3. View service logs:");
    Console.WriteLine("   Get-EventLog -LogName Application -Source 'VardyParty.LocalService' -Newest 10");
    Console.WriteLine("\n4. Check if ports are in use:");
    Console.WriteLine("   netstat -ano | findstr :5019");
    Console.WriteLine("   netstat -ano | findstr :40129");
}
else
{
    Console.WriteLine("\n✅ All tests passed! Service is discoverable and responsive.");
}

Console.WriteLine("\nPress any key to exit...");
Console.ReadKey();
