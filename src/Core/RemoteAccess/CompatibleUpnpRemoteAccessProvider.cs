using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Core.Config;

namespace Core.RemoteAccess;

/// <summary>
/// Compatibility path for otherwise valid IGD routers whose description response
/// Open.Nat cannot decode (some MiniUPnPd firmware returns a quoted charset that
/// HttpContent.ReadAsStringAsync rejects). Open.Nat remains the primary provider.
/// </summary>
internal sealed class CompatibleUpnpRemoteAccessProvider : IRemoteAccessProvider
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private readonly NetworkInfoService m_Network = new();
    private Uri? m_ControlUri;
    private string? m_ServiceType;
    private int m_ExternalPort;

    public async Task<RemoteAccessResult> OpenAsync(
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            IPAddress gateway = m_Network.GetGatewayIp()
                ?? throw new InvalidOperationException("No IPv4 gateway was detected.");
            Uri descriptionUri = await DiscoverDescriptionAsync(gateway, timeout.Token);
            var (controlUri, serviceType) = await ReadControlServiceAsync(
                descriptionUri, timeout.Token);

            string externalText = await InvokeForValueAsync(
                controlUri,
                serviceType,
                "GetExternalIPAddress",
                Array.Empty<KeyValuePair<string, string>>(),
                "NewExternalIPAddress",
                timeout.Token);
            IPAddress? externalIp = IPAddress.TryParse(externalText, out var parsed)
                ? parsed
                : null;
            if (externalIp is null
                || externalIp.Equals(IPAddress.Any)
                || externalIp.Equals(IPAddress.None))
            {
                externalIp = await PublicIpAddressService.GetAsync(timeout.Token);
            }
            if (!RemoteUrlHelper.TryCreate(
                    externalIp, config.ExternalPort, out string url, out string reason))
                return RemoteAccessResult.Manual(reason);

            var arguments = new[]
            {
                Pair("NewRemoteHost", ""),
                Pair("NewExternalPort", config.ExternalPort),
                Pair("NewProtocol", "TCP"),
                Pair("NewInternalPort", config.Port),
                Pair("NewInternalClient", m_Network.GetLanIp()),
                Pair("NewEnabled", 1),
                Pair("NewPortMappingDescription", "CouchDesk"),
                Pair("NewLeaseDuration", 0)
            };
            await InvokeAsync(
                controlUri, serviceType, "AddPortMapping", arguments, timeout.Token);

            m_ControlUri = controlUri;
            m_ServiceType = serviceType;
            m_ExternalPort = config.ExternalPort;
            return RemoteAccessResult.Active(url);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RemoteAccessResult.Manual(
                "The router's UPnP compatibility request timed out.");
        }
        catch (Exception ex)
        {
            return RemoteAccessResult.Manual(
                $"The router was discovered, but its UPnP control request failed ({ex.Message}).");
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (m_ControlUri is null || m_ServiceType is null || m_ExternalPort == 0)
            return;

        try
        {
            var arguments = new[]
            {
                Pair("NewRemoteHost", ""),
                Pair("NewExternalPort", m_ExternalPort),
                Pair("NewProtocol", "TCP")
            };
            await InvokeAsync(
                m_ControlUri,
                m_ServiceType,
                "DeletePortMapping",
                arguments,
                cancellationToken);
        }
        catch
        {
            // The router may already have expired or removed the mapping.
        }
        finally
        {
            m_ControlUri = null;
            m_ServiceType = null;
            m_ExternalPort = 0;
        }
    }

    private async Task<Uri> DiscoverDescriptionAsync(
        IPAddress gateway,
        CancellationToken cancellationToken)
    {
        IPAddress lanIp = m_Network.GetLanIp();
        using var udp = new UdpClient(new IPEndPoint(lanIp, 0));
        string request =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 3\r\n" +
            "ST: urn:schemas-upnp-org:service:WANIPConnection:1\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

        // SSDP uses UDP; a few sends make discovery reliable without extending
        // the overall timeout.
        for (int attempt = 0; attempt < 3; attempt++)
            await udp.SendAsync(bytes, bytes.Length, multicast);

        while (true)
        {
            UdpReceiveResult response = await udp.ReceiveAsync(cancellationToken);
            if (!response.RemoteEndPoint.Address.Equals(gateway)) continue;

            string text = Encoding.UTF8.GetString(response.Buffer);
            string? location = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2
                    && parts[0].Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
                .Select(parts => parts[1].Trim())
                .FirstOrDefault();
            if (Uri.TryCreate(location, UriKind.Absolute, out var uri)) return uri;
        }
    }

    private static async Task<(Uri ControlUri, string ServiceType)> ReadControlServiceAsync(
        Uri descriptionUri,
        CancellationToken cancellationToken)
    {
        // Read bytes deliberately: this bypasses malformed/quoted charset metadata
        // found in otherwise valid MiniUPnPd description responses.
        byte[] bytes = await Http.GetByteArrayAsync(descriptionUri, cancellationToken);
        using var stream = new MemoryStream(bytes);
        XDocument document = XDocument.Load(stream);

        var service = document.Descendants()
            .Where(element => element.Name.LocalName == "service")
            .Select(element => new
            {
                Type = element.Elements().FirstOrDefault(child =>
                    child.Name.LocalName == "serviceType")?.Value,
                Control = element.Elements().FirstOrDefault(child =>
                    child.Name.LocalName == "controlURL")?.Value
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Type)
                && !string.IsNullOrWhiteSpace(item.Control)
                && (item.Type.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase)
                    || item.Type.Contains("WANPPPConnection", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Type!.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The UPnP description has no WAN connection control service.");

        return (new Uri(descriptionUri, service.Control!), service.Type!);
    }

    private static async Task<string> InvokeForValueAsync(
        Uri controlUri,
        string serviceType,
        string action,
        IReadOnlyCollection<KeyValuePair<string, string>> arguments,
        string resultElement,
        CancellationToken cancellationToken)
    {
        XDocument response = await InvokeAsync(
            controlUri, serviceType, action, arguments, cancellationToken);
        return response.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == resultElement)?.Value
            ?? "";
    }

    private static async Task<XDocument> InvokeAsync(
        Uri controlUri,
        string serviceType,
        string action,
        IReadOnlyCollection<KeyValuePair<string, string>> arguments,
        CancellationToken cancellationToken)
    {
        XNamespace envelope = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace service = serviceType;
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(envelope + "Envelope",
                new XAttribute(XNamespace.Xmlns + "s", envelope),
                new XAttribute(
                    envelope + "encodingStyle",
                    "http://schemas.xmlsoap.org/soap/encoding/"),
                new XElement(envelope + "Body",
                    new XElement(service + action,
                        arguments.Select(argument =>
                            new XElement(argument.Key, argument.Value))))));

        using var request = new HttpRequestMessage(HttpMethod.Post, controlUri);
        request.Headers.TryAddWithoutValidation(
            "SOAPACTION", $"\"{serviceType}#{action}\"");
        request.Content = new StringContent(
            document.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");

        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = TryReadUpnpError(responseBytes);
            throw new InvalidOperationException(
                $"{action} returned HTTP {(int)response.StatusCode}{detail}");
        }

        if (responseBytes.Length == 0) return new XDocument();
        using var stream = new MemoryStream(responseBytes);
        return XDocument.Load(stream);
    }

    private static string TryReadUpnpError(byte[] responseBytes)
    {
        try
        {
            using var stream = new MemoryStream(responseBytes);
            XDocument document = XDocument.Load(stream);
            string? code = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "errorCode")?.Value;
            string? description = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "errorDescription")?.Value;
            return string.IsNullOrWhiteSpace(code)
                ? ""
                : $" (UPnP {code}: {description})";
        }
        catch
        {
            return "";
        }
    }

    private static KeyValuePair<string, string> Pair(string name, object value) =>
        new(name, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
}
