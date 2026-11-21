using System.Diagnostics;
using System.ComponentModel;

internal sealed class FrameWindow : Form
{
    private const int HistorySeconds = 30;
    private static readonly long HistoryTicks = HistorySeconds * Stopwatch.Frequency;
    private static readonly Color Ink = Color.FromArgb(233, 239, 247);
    private static readonly Color Muted = Color.FromArgb(171, 187, 204);
    private readonly ComboBox displays = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
    private readonly Button pause = new() { Text = "Pause", AutoSize = true, FlatStyle = FlatStyle.Flat };
    private readonly Label status = new() { AutoSize = true, ForeColor = Muted };
    private readonly Label numbers = new() { AutoSize = true, ForeColor = Ink };
    private readonly Label dwmStatus = new() { AutoSize = true, ForeColor = Muted };
    private readonly TimingGraph graph = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer redraw = new() { Interval = 500 };
    private readonly Queue<(long Timestamp, uint Accumulated)> updates = new();
    private readonly Queue<long> lateEvents = new();
    private readonly object sync = new();
    private readonly CancellationTokenSource traceStop = new();
    private readonly bool elevated = DwmLateTrace.IsElevated();
    private Task? traceTask;
    private string? traceError;
    private bool traceReady;
    private CancellationTokenSource? captureStop;
    private Task? captureTask;
    private string? captureError;

    public FrameWindow(string? requestedOutput)
    {
        Text = "Framesy";
        BackColor = Color.FromArgb(18, 24, 34);
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9);
        ClientSize = new Size(564, 268);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8), RowCount = 5, ColumnCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        toolbar.Controls.Add(new Label { Text = "Measure", AutoSize = true, Margin = new Padding(0, 7, 7, 0) });
        toolbar.Controls.Add(displays);
        toolbar.Controls.Add(pause);
        if (!elevated)
        {
            var admin = new Button { Text = "Enable DWM trace", AutoSize = true, FlatStyle = FlatStyle.Flat };
            admin.Click += (_, _) => RelaunchWithTrace();
            toolbar.Controls.Add(admin);
        }
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(numbers, 0, 1);
        layout.Controls.Add(dwmStatus, 0, 2);
        layout.Controls.Add(graph, 0, 3);
        layout.Controls.Add(status, 0, 4);

        var screens = Screen.AllScreens;
        displays.Items.AddRange(screens.Select(screen => screen.DeviceName).Cast<object>().ToArray());
        var measured = screens.FirstOrDefault(screen =>
            string.Equals(screen.DeviceName, requestedOutput, StringComparison.OrdinalIgnoreCase)) ?? Screen.PrimaryScreen!;
        displays.SelectedItem = measured.DeviceName;
        var panelScreen = screens.FirstOrDefault(screen => screen.DeviceName != measured.DeviceName) ?? measured;
        Location = new Point(panelScreen.WorkingArea.Left + 24, panelScreen.WorkingArea.Top + 82);

        Shown += async (_, _) =>
        {
            if (elevated)
                traceTask = Task.Run(ObserveDwm);
            redraw.Start();
            await ChangeCapture(start: true);
        };
        displays.SelectedIndexChanged += async (_, _) =>
        {
            var otherScreen = Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName != (string)displays.SelectedItem!);
            if (otherScreen != null && Screen.FromControl(this).DeviceName != otherScreen.DeviceName)
                Location = new Point(otherScreen.WorkingArea.Left + 24, otherScreen.WorkingArea.Top + 82);
            await ChangeCapture(start: true);
        };
        pause.Click += async (_, _) => await ChangeCapture(start: captureStop == null);
        redraw.Tick += (_, _) => UpdateDisplay();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        redraw.Dispose();
        traceStop.Cancel();
        traceTask?.Wait(600);
        traceStop.Dispose();
        if (captureStop is { } stop)
        {
            stop.Cancel();
            captureTask?.Wait(600);
            stop.Dispose();
        }
        base.OnFormClosed(e);
    }

    private void RelaunchWithTrace()
    {
        var launch = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        launch.ArgumentList.Add((string)displays.SelectedItem!);
        try
        {
            using var elevatedFramesy = Process.Start(launch);
            if (elevatedFramesy != null)
                Close();
        }
        catch (Win32Exception error)
        {
            lock (sync)
                traceError = error.NativeErrorCode == 1223 ? "DWM trace permission cancelled." : "DWM trace could not start.";
        }
    }

    private void ObserveDwm()
    {
        try
        {
            DwmLateTrace.ReadLive(traceStop.Token, stamp =>
            {
                lock (sync)
                {
                    lateEvents.Enqueue(stamp);
                    TrimLateEvents(stamp - HistoryTicks);
                }
            }, () =>
            {
                lock (sync)
                    traceReady = true;
            });
        }
        catch (OperationCanceledException) when (traceStop.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            lock (sync)
                traceError = $"DWM trace unavailable ({error.HResult:X8}).";
        }
    }

    private void TrimLateEvents(long cutoff)
    {
        while (lateEvents.TryPeek(out long first) && first < cutoff)
            lateEvents.Dequeue();
    }

    private async Task ChangeCapture(bool start)
    {
        pause.Enabled = false;
        displays.Enabled = false;

        if (captureStop is { } previous)
        {
            captureStop = null;
            previous.Cancel();
            if (captureTask != null)
                await captureTask;
            previous.Dispose();
        }
        if (IsDisposed)
            return;

        if (start)
        {
            lock (sync)
            {
                updates.Clear();
                captureError = null;
            }

            var display = (string)displays.SelectedItem!;
            var stop = new CancellationTokenSource();
            captureStop = stop;
            captureTask = Task.Run(() => ObserveOutput(display, stop.Token));
        }

        pause.Text = start ? "Pause display" : "Resume display";
        pause.Enabled = true;
        displays.Enabled = true;
        UpdateDisplay();
    }

    private void ObserveOutput(string display, CancellationToken stop)
    {
        try
        {
            DesktopCapture.Read(display, stop, (timestamp, frames) =>
            {
                lock (sync)
                {
                    if (updates.TryPeek(out var oldest) && timestamp < oldest.Timestamp)
                        updates.Clear();

                    updates.Enqueue((timestamp, frames));
                    while (updates.TryPeek(out var first) && timestamp - first.Timestamp > HistoryTicks + Stopwatch.Frequency)
                        updates.Dequeue();
                }
            });
        }
        catch (Exception error)
        {
            lock (sync)
                captureError = $"Capture ended ({error.HResult:X8}). Pause, then resume to reconnect.";
        }
    }

    private void UpdateDisplay()
    {
        long now = Stopwatch.GetTimestamp();
        (long Timestamp, uint Accumulated)[] recent;
        long[] recentLate;
        string lateNote;
        string? error;
        lock (sync)
        {
            recent = updates.Where(item => now - item.Timestamp <= HistoryTicks).ToArray();
            TrimLateEvents(now - HistoryTicks);
            recentLate = lateEvents.ToArray();
            lateNote = traceError ?? (traceReady
                ? $"DWM late renders (all displays)   {recentLate.Length} · pink ticks"
                : elevated ? "Starting DWM trace…" : "DWM trace off · administrator access required");
            error = captureError;
        }

        if (dwmStatus.Text != lateNote)
            dwmStatus.Text = lateNote;

        var intervals = new List<Interval>();
        ulong coalesced = 0;
        int pastSecond = 0;
        int longGaps = 0;
        for (int index = 0; index < recent.Length; index++)
        {
            var item = recent[index];
            if (now - item.Timestamp <= Stopwatch.Frequency)
                pastSecond++;

            if (item.Accumulated > 1)
            {
                coalesced += item.Accumulated - 1;
                continue;
            }
            if (index == 0)
                continue;

            double gap = (item.Timestamp - recent[index - 1].Timestamp) * 1000.0 / Stopwatch.Frequency;
            if (gap >= 250)
                longGaps++;
            else if (gap > 0)
                intervals.Add(new Interval(item.Timestamp, gap));
        }

        var max = intervals.Count > 0 ? $"{intervals.Max(item => item.Milliseconds):0.0} ms max" : "— ms max";
        var summary = $"{pastSecond}/s observed updates    {max} in chart";
        if (numbers.Text != summary)
            numbers.Text = summary;

        string sameScreen = Screen.FromControl(this).DeviceName == (string?)displays.SelectedItem
            ? "\nOSD is on the measured screen" : string.Empty;
        string note = captureStop == null ? "Paused" : error ??
            $"Last {HistorySeconds} s · {longGaps} gaps ≥250 ms · {coalesced} coalesced{sameScreen}";
        if (status.Text != note)
            status.Text = note;

        graph.SetSamples(intervals, recentLate, now);
    }

    private readonly record struct Interval(long Timestamp, double Milliseconds);

    private sealed class TimingGraph : Control
    {
        private static readonly int[] Guides = [17, 33, 50, 75, 100];
        private IReadOnlyList<Interval> intervals = [];
        private long[] late = [];
        private long now;

        public TimingGraph()
        {
            DoubleBuffered = true;
            Margin = new Padding(0, 8, 0, 5);
        }

        public void SetSamples(IReadOnlyList<Interval> points, long[] lateTimestamps, long timestamp)
        {
            bool changed = intervals.Count != 0 || points.Count != 0 || late.Length != 0 || lateTimestamps.Length != 0;
            intervals = points;
            late = lateTimestamps;
            now = timestamp;
            if (changed)
                Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float gutter = 58 * DeviceDpi / 96f;
            float height = ClientSize.Height - Font.Height - 7;
            float width = ClientSize.Width - gutter - 2;
            if (height <= 0 || width <= 0)
                return;

            using var background = new SolidBrush(Color.FromArgb(12, 17, 25));
            using var grid = new Pen(Color.FromArgb(53, 67, 86));
            using var normal = new Pen(Color.FromArgb(104, 209, 182));
            using var slow = new Pen(Color.FromArgb(255, 176, 100));
            using var lateMarker = new Pen(Color.FromArgb(250, 125, 178), 2);
            e.Graphics.FillRectangle(background, gutter, 2, width, height);

            for (int seconds = HistorySeconds; seconds >= 0; seconds -= 10)
            {
                float x = gutter + (1 - (float)seconds / HistorySeconds) * width;
                e.Graphics.DrawLine(grid, x, 2, x, height + 2);
                string axisLabel = seconds == 0 ? "Now" : $"-{seconds}s";
                int labelWidth = TextRenderer.MeasureText(e.Graphics, axisLabel, Font).Width;
                int labelX = Math.Min((int)x, ClientSize.Width - labelWidth);
                TextRenderer.DrawText(e.Graphics, axisLabel, Font,
                    new Point(labelX, (int)height + 3), Muted, TextFormatFlags.NoPadding);
            }

            foreach (int milliseconds in Guides)
            {
                float y = height - milliseconds / 105f * height + 2;
                e.Graphics.DrawLine(grid, gutter, y, gutter + width, y);
                TextRenderer.DrawText(e.Graphics, $"{milliseconds} ms", Font,
                    new Rectangle(0, (int)y - Font.Height / 2, (int)gutter - 4, Font.Height), Muted,
                    TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }

            foreach (var interval in intervals)
            {
                double age = (now - interval.Timestamp) / (double)Stopwatch.Frequency;
                float x = gutter + (float)(1 - age / HistorySeconds) * width;
                float y = height - (float)Math.Min(interval.Milliseconds / 105, 1) * height + 2;
                e.Graphics.DrawLine(interval.Milliseconds > 25 ? slow : normal, x, height + 2, x, y);
            }
            foreach (long timestamp in late)
            {
                double age = (now - timestamp) / (double)Stopwatch.Frequency;
                float x = gutter + (float)(1 - age / HistorySeconds) * width;
                e.Graphics.DrawLine(lateMarker, x, 2, x, 10);
            }
        }
    }
}


