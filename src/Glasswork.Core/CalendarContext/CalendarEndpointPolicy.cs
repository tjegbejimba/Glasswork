using System.Net;
using System.Net.Sockets;

namespace Glasswork.Core.CalendarContext;

public static class CalendarEndpointPolicy
{
    public static bool TryValidate(string value, out Uri endpoint)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || string.IsNullOrWhiteSpace(parsed.Host)
            || string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(parsed.Host, out var address)
                && !IsPublicAddress(address))
        {
            endpoint = null!;
            return false;
        }

        endpoint = parsed;
        return true;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
            return IsPublicAddress(address.MapToIPv4());

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] != 10
                && bytes[0] != 127
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99)
                && !(bytes[0] == 198 && bytes[1] is 18 or 19)
                && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                && bytes[0] != 0
                && !(bytes[0] >= 224);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        var isGlobalUnicast = (bytes[0] & 0xE0) == 0x20;
        var isDocumentation = bytes[0] == 0x20
            && bytes[1] == 0x01
            && bytes[2] == 0x0D
            && bytes[3] == 0xB8;
        var isSpecial2001 = bytes[0] == 0x20
            && bytes[1] == 0x01
            && bytes[2] == 0
            && bytes[3] < 0x30;
        var isSixToFour = bytes[0] == 0x20 && bytes[1] == 0x02;
        var isDocumentation3fff = bytes[0] == 0x3F
            && (bytes[1] & 0xF0) == 0xF0;
        return isGlobalUnicast
            && !isDocumentation
            && !isDocumentation3fff
            && !isSpecial2001
            && !isSixToFour
            && !address.IsIPv6LinkLocal
            && !address.IsIPv6Multicast
            && !address.IsIPv6SiteLocal
            && (bytes[0] & 0xFE) != 0xFC;
    }
}
