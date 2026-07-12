using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace Capture.Gpu;

/// <summary>
/// GPU screen capture via DXGI Desktop Duplication (the "DXGI" half of the WGC/DXGI
/// question). AcquireNextFrame hands back the changed desktop as a D3D11 texture that
/// never left the GPU. For this spike we then copy it to a CPU BGRA buffer via a staging
/// texture so the encoder is simple to follow; production keeps it on the GPU (see
/// PROTOTYPE.md). Only the primary output is captured here; multi-monitor just means one
/// source per output index.
///
/// Interop note: written without a Windows compiler in the loop. The Vortice/SharpGen
/// API here is stable and I'm fairly confident in it, but a couple of overload/enum names
/// (flagged inline) may need a small tweak on first build.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopDuplicationSource : IGpuFrameSource
{
    private readonly int m_OutputIndex;
    private ID3D11Device m_Device = null!;
    private ID3D11DeviceContext m_Context = null!;
    private IDXGIOutputDuplication? m_Duplication;
    private ID3D11Texture2D? m_Staging;
    private byte[] m_Buffer = Array.Empty<byte>();

    public int Width { get; private set; }
    public int Height { get; private set; }
    public DesktopCaptureTiming LastTiming { get; private set; }

    public DesktopDuplicationSource(int outputIndex = 0)
    {
        m_OutputIndex = outputIndex;
        CreateDevice();
        CreateDuplication();
    }

    private void CreateDevice()
    {
        // Level_11_1 first, fall back to 11_0. BgraSupport is REQUIRED for the
        // B8G8R8A8 desktop format.
        FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

        // If this two-out overload isn't found, use the out-device-only overload and read
        // m_Device.ImmediateContext for the context.
        D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();

        m_Device = device;
        m_Context = context;
    }

    private void CreateDuplication()
    {
        using var dxgiDevice = m_Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        adapter.EnumOutputs(m_OutputIndex, out IDXGIOutput? outputRaw);
        using IDXGIOutput output = outputRaw
            ?? throw new InvalidOperationException($"No display output at index {m_OutputIndex}.");
        using var output1 = output.QueryInterface<IDXGIOutput1>();

        // DesktopCoordinates is the output's virtual-desktop rectangle.
        var bounds = output.Description.DesktopCoordinates;
        Width = bounds.Right - bounds.Left;
        Height = bounds.Bottom - bounds.Top;

        m_Duplication = output1.DuplicateOutput(m_Device);

        EnsureStaging();
        m_Buffer = new byte[Width * Height * 4];
    }

    private void EnsureStaging()
    {
        m_Staging?.Dispose();
        m_Staging = m_Device.CreateTexture2D(new Texture2DDescription
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,      // CPU-readable
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    public DesktopFrame? TryAcquire(int timeoutMs)
    {
        if (m_Duplication is null) CreateDuplication();

        long acquireStartedAt = Stopwatch.GetTimestamp();
        Result r = m_Duplication!.AcquireNextFrame(
            timeoutMs, out OutduplFrameInfo _, out IDXGIResource? resource);
        long acquireTicks = Stopwatch.GetTimestamp() - acquireStartedAt;

        // Nothing changed within the timeout — the caller treats this as "idle".
        if (r == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            LastTiming = new DesktopCaptureTiming(acquireTicks, 0, 0, 0);
            return null;
        }

        // Access lost: resolution/mode change, fullscreen transition, or the secure
        // (UAC/lock) desktop. Recreate the duplication and report idle for this tick.
        if (r == Vortice.DXGI.ResultCode.AccessLost)
        {
            LastTiming = new DesktopCaptureTiming(acquireTicks, 0, 0, 0);
            RecreateDuplication();
            return null;
        }
        r.CheckError();

        long copyStartedAt = Stopwatch.GetTimestamp();
        try
        {
            using (resource)
            using (var texture = resource!.QueryInterface<ID3D11Texture2D>())
                m_Context.CopyResource(m_Staging!, texture); // GPU→GPU copy into the staging texture
        }
        finally
        {
            m_Duplication.ReleaseFrame();
        }
        long copyTicks = Stopwatch.GetTimestamp() - copyStartedAt;

        // Map the staging texture and copy into a tight, top-down BGRA buffer. RowPitch
        // usually exceeds Width*4 (alignment), so copy row by row.
        long mapStartedAt = Stopwatch.GetTimestamp();
        MappedSubresource map = m_Context.Map(m_Staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        long mapTicks = Stopwatch.GetTimestamp() - mapStartedAt;
        long cpuCopyStartedAt = Stopwatch.GetTimestamp();
        try
        {
            int tight = Width * 4;
            for (int y = 0; y < Height; y++)
                Marshal.Copy(IntPtr.Add(map.DataPointer, y * map.RowPitch), m_Buffer, y * tight, tight);
        }
        finally
        {
            m_Context.Unmap(m_Staging!, 0);
        }
        long cpuCopyTicks = Stopwatch.GetTimestamp() - cpuCopyStartedAt;
        LastTiming = new DesktopCaptureTiming(acquireTicks, copyTicks, mapTicks, cpuCopyTicks);

        return new DesktopFrame { Width = Width, Height = Height, Bgra = m_Buffer, Changed = true };
    }

    private void RecreateDuplication()
    {
        m_Duplication?.Dispose();
        m_Duplication = null;
        try { CreateDuplication(); }
        catch { /* transient during a mode change — next TryAcquire retries */ }
    }

    public void Dispose()
    {
        m_Staging?.Dispose();
        m_Duplication?.Dispose();
        m_Context?.Dispose();
        m_Device?.Dispose();
    }
}
