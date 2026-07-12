using Core.Config;
using Open.Nat;

namespace Core.RemoteAccess;

public sealed class OpenNatRemoteAccessProvider : IRemoteAccessProvider
{
    private readonly SemaphoreSlim m_Gate = new(1, 1);
    private readonly CompatibleUpnpRemoteAccessProvider m_Compatibility = new();
    private NatDevice? m_Device;
    private Mapping? m_Mapping;
    private string? m_CompatibilityUrl;

    public async Task<RemoteAccessResult> OpenAsync(
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        await m_Gate.WaitAsync(cancellationToken);
        try
        {
            if (RemoteUrlHelper.IsValid(m_CompatibilityUrl))
                return RemoteAccessResult.Active(m_CompatibilityUrl);
            if (m_Device is not null
                && m_Mapping is not null
                && RemoteUrlHelper.IsValid(config.LastRemoteUrl))
                return RemoteAccessResult.Active(config.LastRemoteUrl);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var discoverer = new NatDiscoverer();
            // Discover all candidates. DiscoverDeviceAsync returns whichever UPnP
            // or NAT-PMP responder wins a race; on networks with mesh nodes, VPNs,
            // or Internet Connection Sharing that can be a non-gateway device whose
            // GetExternalIPAsync result is null.
            var devices = (await discoverer.DiscoverDevicesAsync(
                    PortMapper.Upnp | PortMapper.Pmp, timeout))
                .ToList();
            if (devices.Count == 0) throw new NatDeviceNotFoundException();

            string lastReason = "No discovered router returned a usable public address.";
            foreach (var device in devices)
            {
                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var externalIp = await device.GetExternalIPAsync();
                    if (!RemoteUrlHelper.TryCreate(
                            externalIp, config.ExternalPort, out string url, out string reason))
                    {
                        lastReason = reason;
                        continue;
                    }

                    var mapping = new Mapping(
                        Protocol.Tcp, config.Port, config.ExternalPort, "CouchDesk");
                    await device.CreatePortMapAsync(mapping);

                    m_Device = device;
                    m_Mapping = mapping;
                    return RemoteAccessResult.Active(url);
                }
                catch (MappingException ex)
                {
                    lastReason = $"A router reported a public address but refused the port mapping ({ex.Message}).";
                }
                catch (Exception ex)
                {
                    lastReason = $"A discovered router could not provide a usable mapping ({ex.Message}).";
                }
            }

            return await TryCompatibilityAsync(config, lastReason, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await TryCompatibilityAsync(
                config, "Open.Nat discovery timed out.", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NatDeviceNotFoundException)
        {
            return await TryCompatibilityAsync(
                config, "Open.Nat did not produce a usable gateway device.", cancellationToken);
        }
        catch (MappingException ex)
        {
            return await TryCompatibilityAsync(
                config, $"Open.Nat mapping failed: {ex.Message}", cancellationToken);
        }
        catch (Exception ex)
        {
            return await TryCompatibilityAsync(
                config, $"Open.Nat failed: {ex.Message}", cancellationToken);
        }
        finally
        {
            m_Gate.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await m_Gate.WaitAsync(cancellationToken);
        try
        {
            await m_Compatibility.CloseAsync(cancellationToken);
            if (m_Device is not null && m_Mapping is not null)
            {
                try { await m_Device.DeletePortMapAsync(m_Mapping); }
                catch { /* The lease may already have disappeared with the router. */ }
            }

            m_Device = null;
            m_Mapping = null;
            m_CompatibilityUrl = null;
        }
        finally
        {
            m_Gate.Release();
        }
    }

    private async Task<RemoteAccessResult> TryCompatibilityAsync(
        AppConfig config,
        string openNatReason,
        CancellationToken cancellationToken)
    {
        RemoteAccessResult compatible = await m_Compatibility.OpenAsync(
            config, cancellationToken);
        if (compatible.Success && RemoteUrlHelper.IsValid(compatible.RemoteUrl))
        {
            m_CompatibilityUrl = compatible.RemoteUrl;
            return compatible;
        }

        return RemoteAccessResult.Manual(
            $"{compatible.Message} Open.Nat reported: {openNatReason}");
    }
}
