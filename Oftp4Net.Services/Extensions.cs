using System.Net;
using Oftp4Net.Domain;

namespace Oftp4Net.Services;

public static class Extensions
{
    public static async Task<IPAddress> GetIpAddress(this Partner partner)
    {
        if (IPAddress.TryParse(partner.Host, out var ipAddress)) return ipAddress;
        
        var addresses = await Dns.GetHostAddressesAsync(partner.Host);
        if (addresses.Length == 0)
            throw new Exception($"Cannot resolve host {partner.Host}");
            
        ipAddress = addresses.First();

        return ipAddress;
    }
}