using System.Net;
using System.Net.Sockets;

namespace Customize_ORM.Extensions
{
    public static class NetworkUtils
    {

        /// <summary>
        /// 获取所有IPv4地址
        /// </summary>
        public static List<IPAddress> GetAllLocalIPv4Addresses(bool ignoreLoopback = true)
        {
            var hostEntry = Dns.GetHostEntry(Dns.GetHostName());
            var ipAddresses = hostEntry.AddressList.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            if (ignoreLoopback)
            {
                ipAddresses = ipAddresses.Where(ip => !IPAddress.IsLoopback(ip));
            }
            return ipAddresses.ToList();
        }
    }
}
