using System.Net;
using System.Net.Sockets;

namespace Mailnix.Desktop;

internal static class Ports
{
    /// <summary>Nothing listens on the port on either loopback address ("localhost" binds both).</summary>
    public static bool IsFree(int port) => IsFree(IPAddress.Loopback, port) && (!Socket.OSSupportsIPv6 || IsFree(IPAddress.IPv6Loopback, port));

    /// <summary>A port the system considers free right now.</summary>
    public static int FindFree()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static bool IsFree(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }
}
