using System.Globalization;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        if (args.Length == 4 && args[0] == "--probe")
        {
            RunProbe(args[1], int.Parse(args[2], CultureInfo.InvariantCulture), args[3]);
            return;
        }

        if (args.Length == 3 && args[0] == "--dwm-test")
        {
            TestDwm(args[1], args[2]);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new FrameWindow(args.FirstOrDefault()));
    }

    private static void TestDwm(string source, string destination)
    {
        var stamps = new StringBuilder("qpc\r\n");
        void Add(long timestamp) => stamps.Append(CultureInfo.InvariantCulture, $"{timestamp}\r\n");

        try
        {
            if (int.TryParse(source, out int seconds))
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                DwmLateTrace.ReadLive(stop.Token, Add, () => { });
            }
            else
            {
                DwmLateTrace.ReadFile(source, Add);
            }
            File.WriteAllText(destination, stamps.ToString());
        }
        catch (Exception error)
        {
            File.WriteAllText(destination, error.ToString());
            Environment.ExitCode = 1;
        }
    }

    private static void RunProbe(string display, int seconds, string destination)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var log = new StringBuilder("qpc,accumulated_updates\r\n");
        try
        {
            DesktopCapture.Read(display, stop.Token, (timestamp, frames) =>
                log.Append(CultureInfo.InvariantCulture, $"{timestamp},{frames}\r\n"));
            File.WriteAllText(destination, log.ToString());
        }
        catch (Exception error)
        {
            File.WriteAllText(destination, error.ToString());
            Environment.ExitCode = 1;
        }
    }
}

internal static class DesktopCapture
{
    private const int WaitTimeout = unchecked((int)0x887A0027);

    public static void Read(string display, CancellationToken stop, Action<long, uint> onUpdate)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var nextAdapter).Success; adapterIndex++)
        {
            using var adapter = nextAdapter!;
            for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var nextOutput).Success; outputIndex++)
            {
                using var output = nextOutput!;
                if (!output.Description.DeviceName.Equals(display, StringComparison.OrdinalIgnoreCase))
                    continue;

                using var output1 = output.QueryInterface<IDXGIOutput1>();
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None,
                    [FeatureLevel.Level_11_0], out var nextDevice).CheckError();
                using var device = nextDevice!;
                using var duplication = output1.DuplicateOutput(device);

                while (!stop.IsCancellationRequested)
                {
                    var result = duplication.AcquireNextFrame(120, out var info, out var image);
                    if (result.Code == WaitTimeout)
                        continue;

                    result.CheckError();
                    try
                    {
                        // Use metadata only. No pixel readback, copies, recording or network access.
                        image.Dispose();
                        if (info.LastPresentTime > 0)
                            onUpdate(info.LastPresentTime, info.AccumulatedFrames);
                    }
                    finally
                    {
                        duplication.ReleaseFrame().CheckError();
                    }
                }
                return;
            }
        }
        throw new InvalidOperationException($"Windows no longer exposes {display} as an active output.");
    }
}



