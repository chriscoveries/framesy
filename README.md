# Framesy

Framesy is an always-on-top Windows monitor for the timing of desktop image updates. Select a display, reproduce a hitch while scrolling or dragging a window, and look for a spike in the 30-second graph.

## What the graph measures

- **Observed updates/s** counts the desktop image updates Framesy received in the past second. A still desktop may show zero even as the monitor continues refreshing.
- **Frame intervals** are the elapsed milliseconds between consecutive desktop image updates as timestamped by Windows. At 60 Hz, one refresh takes about 16.7 ms. Amber bars mark intervals above 25 ms, a fixed visual guide. Gaps of at least 250 ms have their own count because idle time can also create long gaps.
- **DWM late renders** are pink ticks for Desktop Window Manager schedule glitches. The event stream covers all displays. The ticks share a timeline with the selected display's update intervals, so you can look for events that coincide. Click **Enable DWM trace** and approve Windows' administrator prompt to collect them.
- **Coalesced** counts updates Windows combined before Framesy acquired them. Framesy leaves the affected intervals off the graph because their individual timestamps are unavailable.

Framesy uses the DXGI Desktop Duplication API. It reads update timestamps and releases the image resource without copying or saving pixels. Pointer-only events are filtered out. An optional real-time ETW session counts DWM schedule glitches without writing a trace file. Desktop updates include app behavior and compositor behavior, so a spike during steady motion gives you a time to investigate. The numbers cannot count physical display drops or isolate a cause by themselves. If you have two screens, keep Framesy on the other screen so the panel's redraws don't become part of the measured desktop. Desktop Duplication itself can affect GPU activity.

## Run

Framesy targets Windows 10 or later on x64 and uses the .NET 8 Desktop Runtime. The default selection is the primary display. Use the dropdown to pick another display, or start Framesy with a Windows display device name, such as `\\.\DISPLAY2`. **Pause display** stops desktop duplication, and **Resume display** restarts it, including after a display mode change. Close the window to end both measurements.

## Build

From PowerShell with the .NET 8 SDK:

```powershell
dotnet publish .\Framesy.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
.\dist\Framesy.exe
```

## Case study · Desktop stutter from an Intel GPU wake-up

On a Windows 10 system, both displays were connected to an RTX 3070 while the Intel UHD 770 remained enabled. Framesy showed repeated gaps of roughly 70 ms. A separate ETW capture with Framesy closed matched 55 of 130 late DWM renders to the Intel GPU waking from D3 to D0.

DWM called into the Intel graphics driver to signal a GPU synchronization object. Its compositor thread then waited 52–69 ms on a graphics-kernel resource. In each matched case, the thread resumed just after the Intel adapter completed its power transition. The user reported an immediate improvement after disabling the iGPU. The trace left the other misses unclassified.

To investigate similar spikes, record DWM late-render events, context-switch and ready-thread stacks, and DxgKrnl runtime power events in the same ETW session. Match the wait interval to the adapter's power request and completion. Check which adapter is involved before changing hardware settings. Disabling an Intel iGPU also makes its Quick Sync engine unavailable.

[Microsoft DXGI frame metadata reference](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info) · [Microsoft DWM schedule glitch definition](https://learn.microsoft.com/en-us/windows-hardware/test/hlk/testref/0933db65-5c40-42d9-874e-763724c9e464)
