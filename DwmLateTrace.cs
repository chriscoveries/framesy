using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using System.Security.Principal;

internal static class DwmLateTrace
{
    // DWM-Core / FrameDrawInconsistencyInformation, SCHEDULE_GLITCH (task 17).
    private static readonly Guid Provider = new("9e9bba3c-2e38-40cb-99f4-9e8281425164");
    private const ulong GlitchKeyword = 0x4;
    private const int GlitchTask = 17;
    private const int GlitchEvent = 24;

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void ReadLive(CancellationToken stop, Action<long> onLate, Action onReady)
    {
        stop.ThrowIfCancellationRequested();
        using var session = new TraceEventSession($"Framesy-Dwm-{Environment.ProcessId}");
        session.StopOnDispose = true;
        var events = session.Source;
        Subscribe(events, onLate);
        session.EnableProvider(Provider, TraceEventLevel.Informational, GlitchKeyword);
        using var cancellation = stop.Register(() => session.Stop());
        onReady();
        events.Process();
    }

    public static void ReadFile(string etl, Action<long> onLate)
    {
        using var events = new ETWTraceEventSource(etl);
        Subscribe(events, onLate);
        events.Process();
    }

    private static void Subscribe(TraceEventDispatcher events, Action<long> onLate)
    {
        events.AllEvents += item =>
        {
            if (item.ProviderGuid == Provider && (int)item.Task == GlitchTask && (int)item.ID == GlitchEvent)
            {
                // Keep the same QPC clock as DXGI LastPresentTime to align both signals.
#pragma warning disable CS0618
                onLate(item.TimeStampQPC);
#pragma warning restore CS0618
            }
        };
    }
}
