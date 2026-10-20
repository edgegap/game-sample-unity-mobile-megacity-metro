using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace Unity.MegacityMetro.Edgegap
{
    /// <summary>
    /// Reads what Edgegap injects into a deployment, and the direct-connect target a client can be given
    /// to join an Edgegap server without Unity Gaming Services.
    /// </summary>
    public static class EdgegapEnvironment
    {
        /// <summary>Client argument: '-edgegapConnect host:port' (host can be an IP or the deployment FQDN).</summary>
        public const string DirectConnectArg = "-edgegapConnect";

        /// <summary>Same as <see cref="DirectConnectArg"/>, for platforms where passing arguments is awkward.</summary>
        public const string DirectConnectEnv = "EDGEGAP_CONNECT";

        /// <summary>True inside an Edgegap deployment (Edgegap sets ARBITRIUM_REQUEST_ID on every deployment).</summary>
        public static bool IsOnEdgegap => !string.IsNullOrEmpty(RequestId);

        public static string RequestId => Environment.GetEnvironmentVariable("ARBITRIUM_REQUEST_ID");
        public static string SelfStopUrl => Environment.GetEnvironmentVariable("ARBITRIUM_DELETE_URL");
        public static string SelfStopToken => Environment.GetEnvironmentVariable("ARBITRIUM_DELETE_TOKEN");

        private static bool s_DirectConnectParsed;
        private static string s_DirectConnectHost;
        private static ushort s_DirectConnectPort;

        public static bool HasDirectConnect => TryGetDirectConnect(out _, out _);

        /// <summary>
        /// Returns the direct-connect target, with the host resolved to an IPv4 address.
        /// </summary>
        public static bool TryGetDirectConnect(out string ip, out ushort port)
        {
            if (!s_DirectConnectParsed)
            {
                s_DirectConnectParsed = true;
                ParseDirectConnect();
            }

            ip = s_DirectConnectHost;
            port = s_DirectConnectPort;
            return !string.IsNullOrEmpty(ip) && port != 0;
        }

        private static void ParseDirectConnect()
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, DirectConnectArg);
            var value = index >= 0 && index + 1 < args.Length
                ? args[index + 1]
                : Environment.GetEnvironmentVariable(DirectConnectEnv);

            if (string.IsNullOrEmpty(value))
                return;

            var separator = value.LastIndexOf(':');
            if (separator <= 0 || !ushort.TryParse(value.Substring(separator + 1), out var port))
            {
                Debug.LogError($"[Edgegap] Cannot parse direct-connect target '{value}', expected 'host:port'.");
                return;
            }

            var host = value.Substring(0, separator);
            if (!IPAddress.TryParse(host, out _))
            {
                try
                {
                    var address = Dns.GetHostAddresses(host)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (address == null)
                    {
                        Debug.LogError($"[Edgegap] '{host}' has no IPv4 address.");
                        return;
                    }

                    host = address.ToString();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Edgegap] Cannot resolve '{host}': {e.Message}");
                    return;
                }
            }

            s_DirectConnectHost = host;
            s_DirectConnectPort = port;
            Debug.Log($"[Edgegap] Direct connect to {host}:{port}, Unity Gaming Services matchmaking is bypassed.");
        }
    }
}
