using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    // Finds the server without being told its address, by going through the whole network.
    //
    // Every address of every network this PC is connected to is asked, in two ways at the same time:
    //
    //   by certificate  An HTTPS handshake on the server port. The server has to present exactly the certificate
    //                   this client is configured to trust and prove it holds the private key. This needs nothing
    //                   special on the server, so it works with every version of the API.
    //   by question     A small UDP packet with a fresh random number in it. A server that runs the search service
    //                   answers with its HTTPS port, signed with the certificate's private key over that number.
    //                   Faster, and it also learns a non-default port.
    //
    // Either way an impostor cannot pass for the server and an old answer cannot be replayed.
    // Machines that fail the check are never sent anything else.
    public static class ServerDiscovery
    {
        private const string RequestMagic = "QAREPO-DISCOVER-1";
        private const string ReplyMagic = "QAREPO-SERVER-1";

        // Subject of the certificate the API creates for itself. Used only to tell the user about a server
        // that looks like ours but is not the one this client was set up to trust.
        private const string OwnCertificateSubject = "CN=QA Repository API";

        // Upper bound on one search, so a PC on an unusually large network does not flood it
        private const int MaxAddresses = 4096;
        private const int HandshakesAtOnce = 384;

        // Returns the address of the first machine that proves it is the trusted server, or null if none did.
        //   onFullSearch  is told which address ranges are being gone through, once the quick round found nothing
        //   lookalikes    receives servers that look like ours but present a different certificate
        public static async Task<Uri> FindAsync(int searchPort, int httpsPort, ICollection<string> trustedThumbprints,
            IReadOnlyList<IPAddress> lastKnown, IReadOnlyList<string> extraNetworks, Action<string> onFullSearch, ConcurrentBag<string> lookalikes)
        {
            using var stop = new CancellationTokenSource();
            var networks = GetNetworks(extraNetworks);
            var fullRound = new TaskCompletionSource<List<IPAddress>>();

            try
            {
                // Quick round: this PC and where the server was last time (both ways), plus one broadcast per network.
                // In daily use this is answered in milliseconds and nothing more is sent.
                var nearby = new[] { IPAddress.Loopback }.Concat(lastKnown).Distinct().ToList();
                var broadcasts = new[] { IPAddress.Broadcast }.Concat(networks.Select(n => n.Broadcast));

                var searches = new List<Task<Uri>>
                {
                    AskByCertificateAsync(nearby, httpsPort, trustedThumbprints, lookalikes, stop.Token),
                    AskByQuestionAsync(searchPort, trustedThumbprints, nearby.Concat(broadcasts).Distinct().ToList(), fullRound.Task, stop.Token)
                };

                var found = await FirstFoundAsync(searches, Task.Delay(400));
                if (found != null) return found;

                // Full round: every address of every connected network, one by one. Unlike a broadcast,
                // this also passes through a VPN.
                var addresses = GetAddresses(networks);
                onFullSearch?.Invoke(Describe(networks, addresses.Count));

                fullRound.SetResult(addresses);
                searches.Add(AskByCertificateAsync(addresses, httpsPort, trustedThumbprints, lookalikes, stop.Token));

                return await FirstFoundAsync(searches, Task.Delay(Timeout.Infinite, stop.Token));
            }
            finally
            {
                fullRound.TrySetResult(new List<IPAddress>());
                stop.Cancel();
            }
        }

        // Waits for the first search that finds the server. Null when all of them finished empty-handed or time is up.
        private static async Task<Uri> FirstFoundAsync(List<Task<Uri>> searches, Task timeLimit)
        {
            var running = searches.ToList();
            while (running.Count > 0)
            {
                var finished = await Task.WhenAny(running.Cast<Task>().Append(timeLimit));
                if (finished == timeLimit) return null;

                var search = (Task<Uri>)finished;
                running.Remove(search);
                if (search.IsCompletedSuccessfully && search.Result != null) return search.Result;
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------- by certificate

        private static async Task<Uri> AskByCertificateAsync(List<IPAddress> addresses, int httpsPort, ICollection<string> trustedThumbprints,
            ConcurrentBag<string> lookalikes, CancellationToken stop)
        {
            Uri found = null;
            using var done = CancellationTokenSource.CreateLinkedTokenSource(stop);

            try
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = HandshakesAtOnce, CancellationToken = done.Token };
                await Parallel.ForEachAsync(addresses, options, async (address, cancel) =>
                {
                    if (await PresentsTrustedCertificateAsync(address, httpsPort, trustedThumbprints, lookalikes, cancel))
                    {
                        found = new Uri($"https://{address}:{httpsPort}/");
                        done.Cancel();
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Found, or the other way of asking got there first
            }

            return found;
        }

        private static async Task<bool> PresentsTrustedCertificateAsync(IPAddress address, int httpsPort, ICollection<string> trustedThumbprints,
            ConcurrentBag<string> lookalikes, CancellationToken stop)
        {
            try
            {
                using var patience = CancellationTokenSource.CreateLinkedTokenSource(stop);

                // Long enough for one retry of the opening packet, which a router may drop while it looks the address up
                patience.CancelAfter(1100);
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(address, httpsPort, patience.Token);

                // Something listens there. The handshake makes it present its certificate and prove it holds the key;
                // nothing else is sent to it unless that certificate is the trusted one.
                patience.CancelAfter(3000);
                using var tls = new SslStream(tcp.GetStream());
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = address.ToString(),
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    {
                        if (certificate == null) return false;

                        var thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                        if (trustedThumbprints.Contains(thumbprint)) return true;

                        if (certificate.Subject == OwnCertificateSubject && !IPAddress.IsLoopback(address))
                            lookalikes.Add($"{address} (thumbprint {thumbprint})");
                        return false;
                    }
                }, patience.Token);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------------------------------ by question

        private static async Task<Uri> AskByQuestionAsync(int searchPort, ICollection<string> trustedThumbprints, List<IPAddress> quickRound,
            Task<List<IPAddress>> fullRound, CancellationToken stop)
        {
            if (searchPort <= 0) return null;

            try
            {
                var nonce = RandomNumberGenerator.GetBytes(16);
                byte[] request = [.. Encoding.ASCII.GetBytes(RequestMagic), .. nonce];

                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };

                // Windows reports "nothing is listening there" from one target as a failure of the whole socket; switch that off
                if (OperatingSystem.IsWindows())
                    udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null);

                using var listening = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var answer = ListenAsync(udp, nonce, trustedThumbprints, listening.Token);

                await SendAsync(udp, request, searchPort, quickRound, answer);

                var addresses = await Task.WhenAny(answer, fullRound) == answer ? null : await fullRound;
                if (addresses != null)
                {
                    // Sent twice: the first packet to an address the network has not talked to recently
                    // can be dropped while that address is being looked up
                    for (var pass = 0; pass < 2 && !answer.IsCompleted; pass++)
                    {
                        await SendAsync(udp, request, searchPort, addresses, answer);
                        await Task.WhenAny(answer, Task.Delay(pass == 0 ? 800 : 1500, stop));
                    }
                }

                listening.Cancel();
                return await answer;
            }
            catch (SocketException)
            {
                return null;
            }
        }

        private static async Task<Uri> ListenAsync(UdpClient udp, byte[] nonce, ICollection<string> trustedThumbprints, CancellationToken stop)
        {
            while (true)
            {
                try
                {
                    var reply = await udp.ReceiveAsync(stop);
                    var httpsPort = VerifyReply(reply.Buffer, nonce, trustedThumbprints);
                    if (httpsPort > 0) return new Uri($"https://{reply.RemoteEndPoint.Address}:{httpsPort}/");
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (SocketException)
                {
                    await Task.Delay(50);
                }
            }
        }

        private static async Task SendAsync(UdpClient udp, byte[] request, int port, IEnumerable<IPAddress> targets, Task answer)
        {
            var sent = 0;
            foreach (var target in targets)
            {
                if (answer.IsCompleted) return;

                try
                {
                    udp.Send(request, request.Length, new IPEndPoint(target, port));
                }
                catch (SocketException)
                {
                    // That network is down or does not allow this; the others may still work
                }

                // A short breath every few dozen packets keeps the search gentle on the network
                if (++sent % 50 == 0) await Task.Delay(10);
            }
        }

        // Returns the server's HTTPS port when the answer is genuine, otherwise 0
        private static int VerifyReply(byte[] reply, byte[] nonce, ICollection<string> trustedThumbprints)
        {
            try
            {
                var magic = Encoding.ASCII.GetBytes(ReplyMagic);
                var offset = magic.Length;
                if (reply.Length < offset + 4 || !reply.AsSpan(0, offset).SequenceEqual(magic)) return 0;

                var port = reply.AsSpan(offset, 2).ToArray();
                var signatureLength = (reply[offset + 2] << 8) | reply[offset + 3];
                offset += 4;
                if (reply.Length <= offset + signatureLength) return 0;

                var signature = reply.AsSpan(offset, signatureLength).ToArray();
                using var certificate = X509CertificateLoader.LoadCertificate(reply.AsSpan(offset + signatureLength));

                // Identification, part 1: it must be exactly the certificate this client was told to trust
                if (!trustedThumbprints.Contains(certificate.GetCertHashString(HashAlgorithmName.SHA256))) return 0;

                // Part 2: the responder must prove it holds that certificate's private key, for our random number
                byte[] signed = [.. magic, .. nonce, .. port];
                using var rsa = certificate.GetRSAPublicKey();
                using var ecdsa = certificate.GetECDsaPublicKey();
                var genuine = rsa != null
                    ? rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                    : ecdsa != null && ecdsa.VerifyData(signed, signature, HashAlgorithmName.SHA256);

                return genuine ? (port[0] << 8) | port[1] : 0;
            }
            catch (CryptographicException)
            {
                return 0;
            }
        }

        // ------------------------------------------------------------------------------- which addresses to ask

        private sealed record Network(uint First, uint Last, IPAddress Broadcast);

        // The address pools to go through: the network of every connected adapter, and the network around each
        // adapter's gateway and DNS server (a VPN often gives the PC an address from one pool while the servers
        // sit in another, next to those). Only private ranges: the search never leaves for the public internet.
        //
        // extraNetworks ("10.0.1.0/24") are pools named in the settings, for servers that live in a network this PC
        // is not part of. They are gone through first.
        private static List<Network> GetNetworks(IReadOnlyList<string> extraNetworks)
        {
            var networks = new List<Network>();

            void Add(IPAddress address, int prefixLength, int widest = 22)
            {
                if (address.AddressFamily != AddressFamily.InterNetwork) return;

                var ip = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
                var isPrivate = ip >> 24 == 10 || ip >> 20 == 0xAC1 || ip >> 16 == 0xC0A8;
                if (!isPrivate) return;

                // Whole pool for the usual office sizes (up to 1022 addresses). A point-to-point VPN link has no
                // pool of its own, so the 254 addresses around it are used. Larger networks are covered by the
                // broadcast and by the 1022 addresses nearest to this PC.
                var mask = uint.MaxValue << (32 - Math.Clamp(prefixLength, widest, Math.Max(widest, 24)));

                // The broadcast address belongs to the network's real size, whatever part of it is gone through one by one
                var realMask = prefixLength <= 0 ? 0 : uint.MaxValue << (32 - Math.Min(prefixLength, 32));

                var network = new Network((ip & mask) + 1, (ip | ~mask) - 1, ToAddress(ip | ~realMask));

                // The same range reached twice (a VPN address and its DNS server, say) is kept once
                if (!networks.Any(other => other.First == network.First && other.Last == network.Last)) networks.Add(network);
            }

            foreach (var range in extraNetworks)
            {
                var parts = range.Split('/');
                if (parts.Length == 2 && IPAddress.TryParse(parts[0].Trim(), out var address) && int.TryParse(parts[1], out var prefixLength))
                    Add(address, prefixLength, widest: 20);
            }

            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var properties = adapter.GetIPProperties();
                foreach (var address in properties.UnicastAddresses) Add(address.Address, address.PrefixLength);
                foreach (var gateway in properties.GatewayAddresses) Add(gateway.Address, 24);
                foreach (var dns in properties.DnsAddresses) Add(dns, 24);
            }

            // A gateway usually sits inside its adapter's own network; drop ranges that another one already covers
            return networks.Where(inner => !networks.Any(outer =>
                outer.First <= inner.First && outer.Last >= inner.Last && (outer.First < inner.First || outer.Last > inner.Last))).ToList();
        }

        private static List<IPAddress> GetAddresses(List<Network> networks)
        {
            var seen = new HashSet<uint>();
            var addresses = new List<IPAddress>();

            foreach (var network in networks)
                for (var ip = network.First; ip <= network.Last && addresses.Count < MaxAddresses; ip++)
                    if (seen.Add(ip)) addresses.Add(ToAddress(ip));

            return addresses;
        }

        private static string Describe(List<Network> networks, int count)
        {
            var ranges = networks.Select(n => $"{ToAddress(n.First)} to {ToAddress(n.Last)}");
            return $"{string.Join(", ", ranges)} ({count} addresses)";
        }

        private static IPAddress ToAddress(uint ip)
        {
            return new IPAddress(new[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip });
        }
    }
}
