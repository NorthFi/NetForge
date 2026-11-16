// root/selfhost/src/NetForge.Api/RadiusDynamicAuth.cs
// RFC 5176 RADIUS Disconnect-Request transport. The MD5 response authenticator
// is REQUIRED by the RADIUS wire protocol, not a general password-hashing choice.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NetForge.Api;

public sealed record DynamicAuthResult(string Status, string Detail);

public static class RadiusDynamicAuth
{
    private const byte DisconnectRequest = 40;
    private const byte DisconnectAck = 41;
    private const byte DisconnectNak = 42;
    private const int MaxPacket = 4096;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private static byte[] Attribute(byte type, byte[] content)
    {
        if (content.Length > 253) throw new ArgumentException("RADIUS attribute exceeds 253 bytes");
        var output = new byte[content.Length + 2];
        output[0] = type;
        output[1] = checked((byte)output.Length);
        content.CopyTo(output, 2);
        return output;
    }

    public static byte[] CreateDisconnectRequest(
        string username, string sessionId, IPAddress nasAddress, string sharedSecret)
    {
        if (nasAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Only configured IPv4 NAS addresses supported", nameof(nasAddress));
        if (string.IsNullOrWhiteSpace(sharedSecret) || sharedSecret.Length < 16)
            throw new ArgumentException("RADIUS shared secret must be at least 16 characters");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Username and accounting session are required");

        var attrs = new[] {
            Attribute(1, Encoding.UTF8.GetBytes(username)),
            Attribute(44, Encoding.UTF8.GetBytes(sessionId)),
            Attribute(4, nasAddress.GetAddressBytes())
        };
        var attrsLength = attrs.Sum(a => a.Length);
        var size = 20 + attrsLength;
        if (size > MaxPacket) throw new ArgumentException("RADIUS packet too large");

        var packet = new byte[size];
        packet[0] = DisconnectRequest;
        packet[1] = RandomNumberGenerator.GetBytes(1)[0];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), checked((ushort)size));
        var position = 20;
        foreach (var attr in attrs) { attr.CopyTo(packet, position); position += attr.Length; }

        var secret = Encoding.UTF8.GetBytes(sharedSecret);
        try
        {
            // RFC 5176 Request Authenticator:
            // MD5(Code+Identifier+Length+16 zero bytes+Attributes+Secret).
            var material = new byte[size + secret.Length];
            packet.CopyTo(material, 0);
            secret.CopyTo(material, size);
            var signature = MD5.HashData(material);
            signature.CopyTo(packet, 4);
            CryptographicOperations.ZeroMemory(material);
            return packet;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public static bool VerifyResponse(byte[] request, byte[] response, string sharedSecret,
        out string status)
    {
        status = "invalid";
        if (request.Length < 20 || response.Length < 20 || response.Length > MaxPacket)
            return false;
        if (request[0] != DisconnectRequest || response[1] != request[1])
            return false;
        var declared = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2));
        if (declared != response.Length) return false;
        if (response[0] is not (DisconnectAck or DisconnectNak)) return false;

        // RFC 5176 Response Authenticator:
        // MD5(Code+Identifier+Length+Request Authenticator+Response Attributes+Secret)
        var secret = Encoding.UTF8.GetBytes(sharedSecret);
        var buffer = new byte[response.Length + secret.Length];
        try
        {
            response.CopyTo(buffer, 0);
            request.AsSpan(4, 16).CopyTo(buffer.AsSpan(4, 16));
            secret.CopyTo(buffer, response.Length);
            var expected = MD5.HashData(buffer);
            if (!CryptographicOperations.FixedTimeEquals(expected, response.AsSpan(4,16)))
                return false;

            status = response[0] == DisconnectAck ? "ack" : "nak";
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static async Task<DynamicAuthResult> DisconnectAsync(
        string hostIp, int port, string sharedSecret, string username,
        string sessionId, CancellationToken cancellation)
    {
        if (!IPAddress.TryParse(hostIp, out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork ||
            port is < 1 or > 65535)
            return new DynamicAuthResult("error", "Invalid preconfigured NAS address or port");

        var packet = CreateDisconnectRequest(username, sessionId, ip, sharedSecret);
        using var client = new UdpClient(AddressFamily.InterNetwork);
        // Connected UDP socket accepts packets only from the expected NAS/port.
        client.Connect(new IPEndPoint(ip, port));
        try
        {
            // At most one retry to handle a dropped packet. A timeout does NOT
            // prove the session remains connected; check accounting and NAS logs.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await client.SendAsync(packet, cancellation);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(RequestTimeout);
                try
                {
                    var reply = await client.ReceiveAsync(timeout.Token);
                    if (!VerifyResponse(packet, reply.Buffer, sharedSecret, out var status))
                        return new DynamicAuthResult("error", "Untrusted or invalid NAS response authenticator");
                    return status == "ack"
                        ? new DynamicAuthResult("ack", "NAS acknowledged Disconnect-Request; await accounting Stop")
                        : new DynamicAuthResult("nak", "NAS rejected Disconnect-Request; verify session identifiers");
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    // Retry once.
                }
            }
            return new DynamicAuthResult("timeout", "No authenticated Disconnect response; session state unknown");
        }
        catch (SocketException)
        {
            return new DynamicAuthResult("error", "NAS socket unreachable; check routing, firewall and listening port");
        }
    }
}
