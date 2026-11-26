using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace GamepadMonitor;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--dump") { Dump(args[1]); return 0; }
        // Elevated helper mode (enable/disable + software replug, HidHide hide/show)
        if (Privileged.IsHelperCall(args)) return Privileged.Dispatch(args);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// Writes a text report of every detected controller (used for diagnostics: GamepadMonitor.exe --dump out.txt).
    static void Dump(string path)
    {
        var sb = new StringBuilder();
        var hids = HidDevice.EnumerateGamepads(new Dictionary<IntPtr, HidDevice>());
        sb.AppendLine($"Raw-input HID gamepads: {hids.Count}");
        foreach (var h in hids)
            sb.AppendLine($"  {h.InstanceId}  usage {h.UsagePage:X}:{h.Usage:X}  parsed={h.ParseOk}  buttons={h.ButtonRanges.Sum(r => r.Max - r.Min + 1)}  " +
                          $"values=[{string.Join(",", h.Values.Select(v => v.Label))}]");
        var rows = DeviceScanner.Scan(hids.Where(h => h.InstanceId != null).Select(h => h.InstanceId).ToList());
        sb.AppendLine($"Controller rows: {rows.Count}");
        foreach (var r in rows)
        {
            int hidCount = r.Members.Count(m => hids.Any(h => string.Equals(h.InstanceId, m.Id, StringComparison.OrdinalIgnoreCase)));
            sb.AppendLine($"  {r.Name} | {r.Connection} | {r.Vid}:{r.Pid} | disabled={r.AnyDisabled} | xinputStyle={r.XInputStyle} | hidInterfaces={hidCount}");
            foreach (var m in r.Members) sb.AppendLine($"      {(m == r.Top ? "TOP " : "    ")}{m.Id}  class={m.Class}  status=0x{m.Status:X} problem={m.Problem}");
            var loc = UsbPort.Locate(r.Top.Id, out var why);
            sb.AppendLine(loc != null ? $"      software replug: port {loc.Value.Port} via {loc.Value.UsbDeviceId} on hub {loc.Value.HubPath}"
                                      : $"      software replug: not possible ({why})");
            foreach (var m in r.Members.Where(m => m.Id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)))
            {
                var ifPath = HidDevice.InterfacesOf(m.Id).FirstOrDefault();
                int n = 0;
                var d = ifPath == null ? null : HidDevice.OpenDirect(ifPath, m.Id, (_, _, _) => System.Threading.Interlocked.Increment(ref n));
                System.Threading.Thread.Sleep(1000);
                sb.AppendLine($"      direct read {m.Id}: {(d == null ? "could not open" : $"opened, {n} reports in 1 s, sony={d.Sony}, out len={d.OutputLen}")}");
                d?.Dispose();
            }
        }
        for (uint i = 0; i < 4; i++)
        {
            uint res = Native.XInputGetState(i, out var st);
            Native.XInputGetBatteryInformation(i, 0, out var bi);
            sb.AppendLine($"XInput slot {i + 1}: {(res == 0 ? $"connected, battery type {bi.BatteryType} level {bi.BatteryLevel}" : "empty")}");
        }
        File.WriteAllText(path, sb.ToString());
    }
}

sealed class DBPanel : Panel
{
    public DBPanel()
    {
        DoubleBuffered = true; ResizeRedraw = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
}

public sealed class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(30, 31, 34), Card = Color.FromArgb(43, 45, 49), Card2 = Color.FromArgb(56, 59, 65),
        Fg = Color.FromArgb(232, 232, 235), Dim = Color.FromArgb(145, 150, 158), Accent = Color.FromArgb(76, 194, 255),
        Good = Color.FromArgb(59, 209, 111), Warn = Color.FromArgb(255, 170, 60), Bad = Color.FromArgb(240, 85, 85),
        SelBg = Color.FromArgb(38, 72, 104);

    readonly Font fTitle = new("Segoe UI Semibold", 15f), fHead = new("Segoe UI Semibold", 10f), fNorm = new("Segoe UI", 9.5f),
        fSmall = new("Segoe UI", 8.5f), fTile = new("Segoe UI Semibold", 13f), fMono = new("Consolas", 9f), fBtn = new("Segoe UI Semibold", 8f);

    readonly ListView list = new();
    readonly DBPanel devView = new(), xView = new();
    readonly Button btnRefresh = new(), btnAdmin = new(), btnToggle = new(), btnCopy = new(), btnHide = new(), btnReplug = new();
    // HID controllers this app reads itself (when Raw Input can't see them, e.g. while hidden by HidHide).
    readonly Dictionary<string, HidDevice> directHids = new(StringComparer.OrdinalIgnoreCase);
    readonly Label lblStatus = new();
    readonly System.Windows.Forms.Timer frameTimer = new() { Interval = 33 }, refreshTimer = new() { Interval = 400 };
    readonly Dictionary<IntPtr, HidDevice> hidByHandle = new();
    readonly XInputMonitor xinput = new();
    readonly bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    List<GamepadRow> rows = new();
    readonly Dictionary<string, List<string>> blockers = new(StringComparer.OrdinalIgnoreCase);
    IntPtr rawBuf = IntPtr.Zero; int rawBufSize;
    int frame;

    float S => DeviceDpi / 96f;
    int D(float v) => (int)Math.Round(v * S);

    public MainForm()
    {
        Text = "Gamepad Monitor" + (isAdmin ? "  [Administrator]" : "");
        BackColor = Bg; ForeColor = Fg; Font = fNorm;
        Size = new Size(D(1280), D(860)); MinimumSize = new Size(D(900), D(600));
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        // ── top bar
        var top = new Panel { Dock = DockStyle.Top, Height = D(44), BackColor = Card, Padding = new Padding(D(8)) };
        StyleButton(btnRefresh, "⟳  Refresh"); btnRefresh.Dock = DockStyle.Left; btnRefresh.Click += (_, _) => RefreshDevices();
        StyleButton(btnAdmin, "🛡  Restart as administrator"); btnAdmin.Dock = DockStyle.Left; btnAdmin.Visible = !isAdmin;
        btnAdmin.Click += (_, _) => RestartElevated();
        lblStatus.Dock = DockStyle.Fill; lblStatus.TextAlign = ContentAlignment.MiddleRight; lblStatus.ForeColor = Dim;
        top.Controls.Add(lblStatus); top.Controls.Add(btnAdmin); top.Controls.Add(new Panel { Dock = DockStyle.Left, Width = D(8) }); top.Controls.Add(btnRefresh);

        // ── device list
        list.View = View.Details; list.FullRowSelect = true; list.MultiSelect = false; list.HideSelection = false;
        list.OwnerDraw = true; list.BorderStyle = BorderStyle.None; list.BackColor = Card; list.ForeColor = Fg;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable; list.Dock = DockStyle.Fill;
        foreach (var (t, w) in new[] { ("Controller", 138), ("State", 90), ("Via", 70), ("Input", 82), ("Rate", 62), ("Interval", 64), ("Last", 56), ("", 4) })
            list.Columns.Add(t, D(w));
        list.Resize += (_, _) => FitFillerColumn();
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(list, true);
        list.DrawColumnHeader += (_, e) =>
        {
            using var b = new SolidBrush(Card2); e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header!.Text, fHead, Rectangle.Inflate(e.Bounds, -D(6), 0), Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
        list.DrawItem += (_, _) => { };
        list.DrawSubItem += DrawListCell;
        list.SelectedIndexChanged += (_, _) => { UpdateButtons(); devView.Invalidate(); };

        // ── detail
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = D(46), BackColor = Bg, Padding = new Padding(D(10), D(8), 0, 0) };
        StyleButton(btnToggle, "Disable"); btnToggle.Click += (_, _) => ToggleSelected();
        StyleButton(btnCopy, "Copy device ID"); btnCopy.Click += (_, _) => { var r = SelectedRow; if (r != null) Clipboard.SetText(r.Top.Id); };
        StyleButton(btnHide, "Hide from games"); btnHide.Click += (_, _) => ToggleHidden();
        StyleButton(btnReplug, "⟲  Replug now"); btnReplug.ForeColor = Warn; btnReplug.Click += (_, _) => ReplugSelected();
        actions.Controls.Add(btnToggle); actions.Controls.Add(btnReplug); actions.Controls.Add(btnHide); actions.Controls.Add(btnCopy);
        devView.Dock = DockStyle.Fill; devView.BackColor = Bg; devView.Paint += (_, e) => PaintDevice(e.Graphics);
        var detail = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        detail.Controls.Add(devView); detail.Controls.Add(actions);

        var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Card2, SplitterWidth = D(3), FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(list); split.Panel2.Controls.Add(detail);

        // ── live inputs strip (every controller)
        xView.Dock = DockStyle.Bottom; xView.Height = D(260); xView.BackColor = Bg;
        xView.Paint += (_, e) => PaintInputs(e.Graphics);
        xView.MouseDown += (_, e) =>
        {
            foreach (var (rect, on) in rumbleHits.ToList())
                if (rect.Contains(e.Location))
                {
                    var err = on(true);
                    if (err == null) activeRumbles.Add(on);
                    else if (err.Length > 0) MessageBox.Show(this, err, "Rumble", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            foreach (var (rect, key) in cardHits)
                if (rect.Contains(e.Location))
                    foreach (ListViewItem it in list.Items)
                        if (((GamepadRow)it.Tag).Key == key) { it.Selected = true; it.EnsureVisible(); return; }
        };
        xView.MouseUp += (_, _) => StopRumbles();
        xView.MouseLeave += (_, _) => StopRumbles();

        Controls.Add(split); Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = D(2), BackColor = Card2 }); Controls.Add(xView); Controls.Add(top);
        Shown += (_, _) => { split.SplitterDistance = Math.Max(D(420), split.Width * 46 / 100); FitFillerColumn(); };

        frameTimer.Tick += (_, _) => OnFrame();
        refreshTimer.Tick += (_, _) => { refreshTimer.Stop(); RefreshDevices(); };
        UpdateButtons();
    }

    void StopRumbles()
    {
        foreach (var on in activeRumbles) on(false);
        activeRumbles.Clear();
        for (int i = 0; i < 4; i++) xinput.Rumble(i, false);
    }

    void FitFillerColumn()
    {
        int used = 0;
        for (int i = 0; i < list.Columns.Count - 1; i++) used += list.Columns[i].Width;
        list.Columns[^1].Width = Math.Max(0, list.ClientSize.Width - used);
    }

    void StyleButton(Button b, string text)
    {
        b.Text = text; b.FlatStyle = FlatStyle.Flat; b.BackColor = Card2; b.ForeColor = Fg; b.Font = fHead;
        b.FlatAppearance.BorderColor = Color.FromArgb(75, 79, 86); b.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 74, 82);
        b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink; b.Padding = new Padding(D(8), D(2), D(8), D(2)); b.Cursor = Cursors.Hand;
    }

    // ───────────── lifecycle ─────────────
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var devs = new[]
        {
            new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 4, dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY, hwndTarget = Handle },
            new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 5, dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY, hwndTarget = Handle },
        };
        if (!Native.RegisterRawInputDevices(devs, 2, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            MessageBox.Show(this, "RegisterRawInputDevices failed: " + new Win32Exception().Message, Text);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Native.timeBeginPeriod(1);
        xinput.Start();
        RefreshDevices();
        frameTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        frameTimer.Stop();
        StopRumbles();
        xinput.Dispose();
        Native.timeEndPeriod(1);
        foreach (var h in hidByHandle.Values) h.Dispose();
        foreach (var h in directHids.Values) h.Dispose();
        if (rawBuf != IntPtr.Zero) Marshal.FreeHGlobal(rawBuf);
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case 0x00FF: HandleRawInput(m.LParam); break;           // WM_INPUT
            case 0x00FE: case 0x0219: refreshTimer.Stop(); refreshTimer.Start(); break; // WM_INPUT_DEVICE_CHANGE / WM_DEVICECHANGE
        }
        base.WndProc(ref m);
    }

    void HandleRawInput(IntPtr hRaw)
    {
        long now = Stopwatch.GetTimestamp();
        uint hdr = (uint)(8 + 2 * IntPtr.Size), size = 0;
        Native.GetRawInputData(hRaw, Native.RID_INPUT, IntPtr.Zero, ref size, hdr);
        if (size == 0) return;
        if (size > rawBufSize) { if (rawBuf != IntPtr.Zero) Marshal.FreeHGlobal(rawBuf); rawBuf = Marshal.AllocHGlobal((int)size); rawBufSize = (int)size; }
        if ((int)Native.GetRawInputData(hRaw, Native.RID_INPUT, rawBuf, ref size, hdr) <= 0) return;
        if (Marshal.ReadInt32(rawBuf, 0) != Native.RIM_TYPEHID) return;
        var hDev = Marshal.ReadIntPtr(rawBuf, 8);
        if (!hidByHandle.TryGetValue(hDev, out var dev)) { refreshTimer.Stop(); refreshTimer.Start(); return; }
        int off = (int)hdr;
        int sizeHid = Marshal.ReadInt32(rawBuf, off), count = Marshal.ReadInt32(rawBuf, off + 4);
        if (sizeHid <= 0 || count <= 0 || 8 + off + (long)sizeHid * count > size) return;
        for (int i = 0; i < count; i++)
        {
            var report = new byte[sizeHid];
            Marshal.Copy(rawBuf + off + 8 + i * sizeHid, report, 0, sizeHid);
            dev.Process(report, now);
        }
    }

    void RefreshDevices()
    {
        string selKey = SelectedRow?.Key;

        var current = HidDevice.EnumerateGamepads(hidByHandle);
        var alive = current.Select(d => d.Handle).ToHashSet();
        foreach (var dead in hidByHandle.Keys.Where(k => !alive.Contains(k)).ToList()) { hidByHandle[dead].Dispose(); hidByHandle.Remove(dead); }
        foreach (var d in current) hidByHandle[d.Handle] = d;

        var hidByInst = new Dictionary<string, List<HidDevice>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in hidByHandle.Values.Where(d => d.InstanceId != null))
        {
            if (!hidByInst.TryGetValue(d.InstanceId, out var l)) hidByInst[d.InstanceId] = l = new();
            l.Add(d);
        }

        rows = DeviceScanner.Scan(hidByInst.Keys);
        var hidden = HidHide.HiddenIds();
        var wantDirect = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            r.Hidden = r.Members.Any(m => hidden.Contains(m.Id));
            foreach (var m in r.Members)
            {
                if (hidByInst.TryGetValue(m.Id, out var l)) { r.Hids.AddRange(l); continue; }
                // Raw Input can't see this HID node (e.g. HidHide hides it) — read it ourselves; we're allow-listed.
                if (m.Disabled || !m.Id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)) continue;
                wantDirect.Add(m.Id);
                if (!directHids.TryGetValue(m.Id, out var d))
                {
                    var path = HidDevice.InterfacesOf(m.Id).FirstOrDefault();
                    d = path == null ? null : HidDevice.OpenDirect(path, m.Id,
                        (dev, report, ts) => { try { BeginInvoke(() => dev.Process(report, ts)); } catch { } });
                    if (d != null) directHids[m.Id] = d;
                }
                if (d != null) r.Hids.Add(d);
            }
            // A disable left pending earlier (e.g. before this app started): look up who blocked it.
            if (r.AnyPendingDisable && !blockers.ContainsKey(r.Key))
                blockers[r.Key] = FindBlockers(DateTime.Now.AddDays(-7), r.Members.Select(m => m.Id));
            if (!r.AnyPendingDisable) blockers.Remove(r.Key);
        }
        foreach (var gone in directHids.Keys.Where(k => !wantDirect.Contains(k)).ToList())
        {
            directHids[gone].Dispose();
            directHids.Remove(gone);
        }

        list.BeginUpdate();
        list.Items.Clear();
        foreach (var r in rows)
        {
            var it = new ListViewItem(r.Name) { Tag = r };
            for (int i = 1; i < list.Columns.Count; i++) it.SubItems.Add("");
            list.Items.Add(it);
            UpdateItem(it, Stopwatch.GetTimestamp());
            if (r.Key == selKey) it.Selected = true;
        }
        if (list.SelectedItems.Count == 0 && list.Items.Count > 0) list.Items[0].Selected = true;
        list.EndUpdate();
        UpdateButtons();
        UpdateStatus();
        devView.Invalidate();
    }

    void UpdateStatus()
    {
        int xc = xinput.Slots.Count(s => { lock (s) return s.Connected; });
        lblStatus.Text = $"{rows.Count} controller(s) found  ·  {xc}/4 XInput slots in use  ·  " +
                         (isAdmin ? "running as administrator" : "not elevated (Windows will ask for permission when you enable/disable)");
    }

    GamepadRow SelectedRow => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as GamepadRow : null;

    void UpdateButtons()
    {
        var r = SelectedRow;
        btnToggle.Enabled = btnCopy.Enabled = r != null;
        bool en = r != null && r.WantsEnable;
        btnToggle.Text = en ? (r.AnyPendingDisable ? "✔  Enable (cancel pending disable)" : "✔  Enable") : "⛔  Disable";
        btnToggle.ForeColor = en ? Good : Warn;
        btnReplug.Visible = r != null && (r.AnyPendingDisable || r.AnyPendingEnable);
        btnReplug.Text = r != null && r.AnyPendingEnable ? "⟲  Replug now (finish enable)" : "⟲  Replug now (finish disable)";
        btnHide.Enabled = r != null && !r.AnyDisabled;
        btnHide.Text = r != null && r.Hidden ? "👁  Show to games" : "🙈  Hide from games";
        btnHide.ForeColor = r != null && r.Hidden ? Good : Accent;
    }

    // ───────────── which controller is in which XInput slot ─────────────
    // Windows doesn't say, so match each slot's stick positions against the HID axes of
    // each Xbox-style controller. Once matched, the assignment sticks until something disconnects.
    readonly string[] slotKey = new string[4];

    int SlotOf(GamepadRow r) => Array.IndexOf(slotKey, r.Key);

    GamepadRow RowByKey(string key) => key == null ? null : rows.FirstOrDefault(r => r.Key == key);

    void UpdateSlotMapping()
    {
        var cands = rows.Where(r => r.XInputStyle && !r.AnyDisabled).ToList();
        var conn = new bool[4]; var pads = new Native.XINPUT_GAMEPAD[4];
        for (int i = 0; i < 4; i++) { var s = xinput.Slots[i]; lock (s) { conn[i] = s.Connected; pads[i] = s.State.Gamepad; } }
        for (int i = 0; i < 4; i++)
            if (!conn[i] || (slotKey[i] != null && !cands.Any(c => c.Key == slotKey[i]))) slotKey[i] = null;

        if (conn.Count(c => c) == 1 && cands.Count == 1) { slotKey[Array.IndexOf(conn, true)] = cands[0].Key; return; }

        for (int i = 0; i < 4; i++)
        {
            if (!conn[i]) continue;
            var gp = pads[i];
            int maxDev = new[] { gp.sThumbLX, gp.sThumbLY, gp.sThumbRX, gp.sThumbRY }.Max(v => Math.Abs((int)v));
            if (maxDev < 9000) continue;   // centered sticks look the same on every pad
            double best = 9, second = 9; GamepadRow bestRow = null;
            foreach (var c in cands)
            {
                double d = c.Hids.Select(h => StickDistance(h, gp)).DefaultIfEmpty(9).Min();
                if (d < best) { second = best; best = d; bestRow = c; } else if (d < second) second = d;
            }
            if (bestRow != null && best < 0.08 && second > best * 2 + 0.04)
            {
                for (int j = 0; j < 4; j++) if (slotKey[j] == bestRow.Key) slotKey[j] = null;
                slotKey[i] = bestRow.Key;
            }
        }
    }

    static double StickDistance(HidDevice h, Native.XINPUT_GAMEPAD gp)
    {
        double? N(ushort usage) => h.Values.FirstOrDefault(v => v.Page == 1 && v.Usage == usage && v.Has)?.Normalized;
        double? x = N(0x30), y = N(0x31), rx = N(0x33), ry = N(0x34);
        if (x == null || y == null || rx == null || ry == null) return 9;
        static double X(short v) => (v + 32768) / 65535.0;
        static double Dd(double hid, double xi) => Math.Min(Math.Abs(hid - xi), Math.Abs(hid - (1 - xi)));  // HID Y may be inverted
        return (Dd(x.Value, X(gp.sThumbLX)) + Dd(y.Value, X(gp.sThumbLY)) + Dd(rx.Value, X(gp.sThumbRX)) + Dd(ry.Value, X(gp.sThumbRY))) / 4;
    }

    string XInputLabel(GamepadRow r)
    {
        if (!r.XInputStyle) return "DInput";
        int s = SlotOf(r);
        return s >= 0 ? $"XInput P{s + 1}" : "XInput";
    }

    void OnFrame()
    {
        frame++;
        UpdateSlotMapping();
        devView.Invalidate();
        xView.Invalidate();
        if (frame % 8 == 0)
        {
            long now = Stopwatch.GetTimestamp();
            foreach (ListViewItem it in list.Items) UpdateItem(it, now);
            if (frame % 64 == 0) UpdateStatus();
        }
    }

    static string FmtAgo(double ms) => ms < 0 ? "never" : ms < 1000 ? $"{ms:0} ms" : ms < 60000 ? $"{ms / 1000:0.0} s" : $"{ms / 60000:0} min";

    void UpdateItem(ListViewItem it, long now)
    {
        var r = (GamepadRow)it.Tag;
        var h = r.ActiveHid;
        var s = h?.Stats.Get(now);
        string[] vals =
        {
            r.Name, r.StateText, r.Connection, XInputLabel(r),
            s == null ? "—" : $"{s.Value.Hz:0} Hz",
            s == null || s.Value.AvgMs == 0 ? "—" : $"{s.Value.AvgMs:0.0} ms",
            s == null ? "—" : FmtAgo(s.Value.SinceLastMs),
        };
        bool changed = false;
        for (int i = 0; i < vals.Length; i++)
            if (it.SubItems[i].Text != vals[i]) { it.SubItems[i].Text = vals[i]; changed = true; }
        if (changed) list.Invalidate(it.Bounds);
    }

    void DrawListCell(object sender, DrawListViewSubItemEventArgs e)
    {
        var r = (GamepadRow)e.Item!.Tag;
        using (var b = new SolidBrush(e.Item.Selected ? SelBg : Card)) e.Graphics.FillRectangle(b, e.Bounds);
        var color = Fg;
        if (e.ColumnIndex == 1) color = r.WantsEnable || r.AnyPendingEnable ? Warn : r.Hidden ? Accent : r.StateText.StartsWith("Enabled") ? Good : Bad;
        else if (e.ColumnIndex >= 2) color = Dim;
        var font = e.ColumnIndex == 0 ? fHead : fNorm;
        TextRenderer.DrawText(e.Graphics, e.SubItem!.Text, font, Rectangle.Inflate(e.Bounds, -D(6), 0), color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    // ───────────── enable / disable ─────────────
    bool disableExplained;

    /// Finishes a pending enable/disable by replugging the controller's USB port in software.
    void ReplugSelected()
    {
        var r = SelectedRow;
        if (r == null) return;
        bool enable = r.AnyPendingEnable;
        var target = r.Members.FirstOrDefault(m => m.PendingDisable || m.PendingEnable) ?? r.Top;
        if (UsbPort.Locate(target.Id, out var why) == null)
        {
            MessageBox.Show(this, $"This controller can't be replugged in software because {why}.\n\nTurn it off and on (or unplug it) to finish.",
                "Replug", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var log = new StringBuilder();
        int code;
        Cursor = Cursors.WaitCursor;
        try
        {
            code = isAdmin ? Privileged.Replug(target.Id, enable, log)
                           : RunElevatedHelper($"--replug {(enable ? "enable" : "disable")} \"{target.Id}\"");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return; }
        finally { Cursor = Cursors.Default; }
        if (code != 0)
            MessageBox.Show(this, "The software replug didn't finish the change. Unplug the controller and plug it back in instead.\n\n" + log,
                "Replug", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        RefreshDevices();
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); RefreshDevices(); };
        t.Start();
    }

    void ToggleSelected()
    {
        var r = SelectedRow;
        if (r == null) return;
        var ok = true; var log = new StringBuilder();
        if (r.WantsEnable)
        {
            // Covers both a real disable and a disable that is still waiting (cancels it).
            bool needsReplug = false;
            foreach (var m in r.Members.Where(m => m.Disabled || m.PendingDisable))
            {
                int code = RunPnputil("enable", m.Id, log);
                ok &= code == 0 || code == 3010;
                needsReplug |= code == 3010;
            }
            blockers.Remove(r.Key);
            if (needsReplug)
                MessageBox.Show(this, "Windows will turn this controller back on the next time it connects.\n\n" +
                    "Unplug it and plug it back in (or switch it off and on). A full PC restart also works but isn't needed.",
                    "Almost done", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            if (!disableExplained)
            {
                var msg = $"Disable \"{r.Name}\" at the driver level?\n\n" +
                          "While disabled, Windows stops the device: games, Steam, Discord, browsers, DS4Windows and this tool's live view " +
                          "will not see it at all. It stays disabled across reboots and reconnects until you click Enable here " +
                          "or enable it in Device Manager.\n\nIf programs are holding it open, a USB controller is replugged in software " +
                          "automatically.\n\n(You won't be asked again until the app restarts.)";
                if (MessageBox.Show(this, msg, "Disable controller", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                disableExplained = true;
            }
            var started = DateTime.Now.AddSeconds(-2);
            int code = RunPnputil("disable", r.Top.Id, log);
            ok = code == 0 || code == 3010;
            if (code == 3010)
            {
                var who = FindBlockers(started, r.Members.Select(m => m.Id));
                blockers[r.Key] = who;
                var list = who.Count > 0 ? string.Join("\n", who.Select(w => "   • " + w)) : "   (Windows didn't log which programs)";
                MessageBox.Show(this,
                    "Windows couldn't switch this controller off right away, because these programs are holding it open" +
                    (r.Connection.StartsWith("Bluetooth") ? " (and a Bluetooth controller can't be replugged in software)" : "") + ":\n\n" + list +
                    "\n\nIt is now marked as disabled and will switch off the next time it connects. To finish without restarting:\n" +
                    "   • Unplug the controller and plug it back in (or turn it off and on), or\n" +
                    "   • close the programs above and click Enable, then Disable again.\n\n" +
                    "Click Enable to cancel the pending disable.",
                    "Disable pending — no restart needed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        if (!ok) MessageBox.Show(this, log.ToString(), "Couldn't change the controller", MessageBoxButtons.OK, MessageBoxIcon.Error);
        RefreshDevices();
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); RefreshDevices(); };
        t.Start();
    }

    /// Reads the "application stopped the removal" events (Kernel-PnP 225) Windows logs when a program vetoes a disable.
    /// Only program names are shown — never command lines, which can contain secrets.
    List<string> FindBlockers(DateTime since, IEnumerable<string> deviceIds)
    {
        var ids = new HashSet<string>(deviceIds, StringComparer.OrdinalIgnoreCase);
        var hits = new List<(DateTime When, int Pid, string Name)>();
        long windowMs = Math.Max(60_000, (long)(DateTime.Now - since).TotalMilliseconds + 60_000);
        for (int attempt = 0; attempt < 3 && hits.Count == 0; attempt++)
        {
            if (attempt > 0) System.Threading.Thread.Sleep(400);
            try
            {
                var q = new EventLogQuery("System", PathType.LogName,
                    $"*[System[Provider[@Name='Microsoft-Windows-Kernel-PnP'] and (EventID=225) and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]");
                using var reader = new EventLogReader(q);
                for (EventRecord e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
                {
                    using (e)
                    {
                        if (e.TimeCreated is not DateTime when || when < since || e.Properties.Count < 5) continue;
                        if (!e.Properties.Skip(3).Any(p => p.Value is string s && ids.Contains(s))) continue;
                        hits.Add((when, Convert.ToInt32(e.Properties[0].Value), Path.GetFileName(e.Properties[2].Value as string ?? "unknown")));
                    }
                }
            }
            catch { break; }
        }
        if (hits.Count == 0) return new List<string>();
        // Keep only the most recent attempt (events from one veto are logged within the same second or two).
        var newest = hits.Max(h => h.When);
        int myPid = Environment.ProcessId;
        return hits.Where(h => h.When >= newest.AddSeconds(-5))
            .Select(h => h.Pid == myPid || h.Name.Equals("GamepadMonitor.exe", StringComparison.OrdinalIgnoreCase)
                ? "Gamepad Monitor (this app — it reads XInput)" : h.Name)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .OrderBy(gp => gp.Key)
            .Select(gp => gp.Count() > 1 ? $"{gp.Key}  (×{gp.Count()})" : gp.Key)
            .ToList();
    }

    /// Enables/disables a device the same way Device Manager does (SetupAPI DIF_PROPERTYCHANGE).
    /// Unlike pnputil, this can also cancel a pending disable on a device that is still running.
    /// Returns 0 = done, 3010 = done once the device reconnects/restarts, other = Win32 error, -1 = could not run.
    int RunPnputil(string verb, string instanceId, StringBuilder log)
    {
        bool enable = verb == "enable";
        try
        {
            int code;
            Cursor = Cursors.WaitCursor;
            try
            {
                code = isAdmin
                    ? Privileged.SetStateAndReplug(instanceId, enable, log)
                    : RunElevatedHelper($"--set-state {verb} \"{instanceId}\"");
            }
            finally { Cursor = Cursors.Default; }
            if (code != 0 && code != 3010)
                log.AppendLine($"Could not {verb} {instanceId}:\n" +
                    (code == unchecked((int)0xE000020B) ? "The device is no longer connected — plug it in and try again."
                     : code == 5 ? "Windows denied permission (administrator rights are required)."
                     : new Win32Exception(code).Message) + $" (code 0x{code:X}).");
            return code;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            log.AppendLine("Cancelled at the Windows permission prompt.");
            return -1;
        }
        // (RunElevatedHelper throws 1223 when the UAC prompt is cancelled.)
        catch (Exception ex)
        {
            log.AppendLine(ex.Message);
            return -1;
        }
    }

    /// Runs this exe elevated (one UAC prompt) with helper arguments and returns its exit code.
    /// Throws Win32Exception 1223 if the prompt is cancelled.
    static int RunElevatedHelper(string args)
    {
        var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args)
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
        p.WaitForExit();
        return p.ExitCode;
    }

    // ───────────── hide from games (HidHide) ─────────────
    void ToggleHidden()
    {
        var r = SelectedRow;
        if (r == null) return;
        if (!HidHide.Installed)
        {
            var res = MessageBox.Show(this,
                "Hiding needs HidHide, a free driver by Nefarius (the author of the ViGEm driver already on this PC, and the one DS4Windows uses).\n\n" +
                "With it, this app can hide a controller from every game and program except the ones you allow, with no unplugging, " +
                "and it can still show the controller's inputs here.\n\n" +
                "Open the official HidHide download page now? Install it, then come back and click Hide again.",
                "HidHide isn't installed", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (res == DialogResult.Yes)
                try { Process.Start(new ProcessStartInfo("https://github.com/nefarius/HidHide/releases/latest") { UseShellExecute = true }); } catch { }
            return;
        }

        bool hide = !r.Hidden;
        // Hide every driver node of the controller (the Xbox USB function and its HID child, etc.).
        var ids = r.Members.Select(m => m.Id).ToArray();
        var log = new StringBuilder();
        int code;
        Cursor = Cursors.WaitCursor;
        try
        {
            if (isAdmin) code = Privileged.HideAndReplug(hide, r.Top.Id, ids, log);
            else
            {
                var logFile = Path.Combine(Path.GetTempPath(), $"GamepadMonitor-hidhide-{Guid.NewGuid():N}.log");
                code = RunElevatedHelper($"--hidhide {(hide ? "hide" : "show")} \"{logFile}\" \"{r.Top.Id}\" " + string.Join(" ", ids.Select(i => $"\"{i}\"")));
                try { if (File.Exists(logFile)) { log.Append(File.ReadAllText(logFile)); File.Delete(logFile); } } catch { }
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { Cursor = Cursors.Default; return; }
        finally { Cursor = Cursors.Default; }

        if (code == 0 || code == Privileged.PendingReconnect)
        {
            HidHide.RememberLocally(ids, hide);
            if (code == Privileged.PendingReconnect)
                MessageBox.Show(this, (hide ? "Hidden" : "Visible again") + " for programs that start using controllers from now on.\n\n" +
                    "Games that were already running may keep their current access until you restart them, or turn the controller off and on.\n\n" + log,
                    "Gamepad Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else MessageBox.Show(this, "HidHide reported a problem:\n\n" + log, "Couldn't change hiding", MessageBoxButtons.OK, MessageBoxIcon.Error);
        RefreshDevices();
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); RefreshDevices(); };
        t.Start();
    }

    void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (Win32Exception) { }
    }

    // ───────────── drawing helpers ─────────────
    void Text_(Graphics g, string s, Font f, Color c, float x, float y)
    {
        TextRenderer.DrawText(g, s, f, new Point((int)x, (int)y), c, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    void TextIn(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags extra = TextFormatFlags.HorizontalCenter)
    {
        TextRenderer.DrawText(g, s, f, r, c, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | extra);
    }

    static GraphicsPath Round(Rectangle r, int rad)
    {
        var p = new GraphicsPath(); int d = rad * 2;
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    void FillRound(Graphics g, Color c, Rectangle r, int rad)
    {
        using var b = new SolidBrush(c); using var p = Round(r, rad); g.FillPath(b, p);
    }

    // ───────────── device detail view ─────────────
    void PaintDevice(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Bg);
        int W = devView.ClientSize.Width, x = D(16), w = W - D(32), y = D(6);
        var r = SelectedRow;
        if (r == null)
        {
            TextIn(g, rows.Count == 0 ? "No game controllers detected. Plug one in or pair it — the list updates automatically." : "Select a controller on the left.",
                fHead, Dim, devView.ClientRectangle);
            return;
        }
        long now = Stopwatch.GetTimestamp();

        Text_(g, r.Name, fTitle, Fg, x, y); y += D(34);
        var stateColor = r.WantsEnable || r.AnyPendingEnable ? Warn : r.Hidden ? Accent : r.StateText.StartsWith("Enabled") ? Good : Bad;
        string st = "● " + r.StateText;
        Text_(g, st, fHead, stateColor, x, y);
        int stW = TextRenderer.MeasureText(st, fHead).Width;
        TextIn(g, $"·   {r.Connection}   ·   VID {r.Vid ?? "?"}  PID {r.Pid ?? "?"}", fNorm, Dim,
            new Rectangle(x + stW + D(8), y, w - stW - D(8), D(20)), TextFormatFlags.Left);
        y += D(24);
        Text_(g, r.Top.Id, fSmall, Dim, x, y); y += D(20);
        string xiNote; Color xiColor = Dim;
        if (!r.XInputStyle)
        {
            xiNote = "Not an XInput controller: games that only support Xbox controllers (XInput) won't see it " +
                     "unless Steam Input or DS4Windows turns it into a virtual Xbox pad.";
            xiColor = Warn;
        }
        else
        {
            int slot = SlotOf(r);
            xiNote = slot >= 0 ? $"XInput controller: games see it as Player {slot + 1}."
                               : "XInput controller: move a stick to find out which player slot it's in.";
        }
        TextRenderer.DrawText(g, xiNote, fSmall, new Rectangle(x, y, w, D(34)), xiColor, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        y += D(r.XInputStyle ? 22 : 36);

        if (r.AnyPendingDisable || r.AnyPendingEnable)
        {
            blockers.TryGetValue(r.Key, out var who);
            string body = r.AnyPendingDisable
                ? "Windows has marked this controller as disabled, but a program is still holding it open, so it keeps working for now. " +
                  "Unplug it and plug it back in (or turn it off and on) to finish — no PC restart needed. Click “Enable” to cancel."
                : "Windows has marked this controller to be enabled again. Unplug it and plug it back in (or turn it off and on) to finish.";
            if (who != null && who.Count > 0) body += "\n\nHolding it open: " + string.Join(", ", who);
            var box = new Rectangle(x, y, w, D(who != null && who.Count > 0 ? 150 : 110));
            FillRound(g, Color.FromArgb(70, 52, 25), box, D(8));
            Text_(g, r.AnyPendingDisable ? "Disable pending — replug to finish" : "Enable pending — replug to finish", fHead, Warn, box.X + D(14), box.Y + D(12));
            TextRenderer.DrawText(g, body, fNorm, new Rectangle(box.X + D(14), box.Y + D(38), box.Width - D(28), box.Height - D(44)), Fg,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            y += box.Height + D(12);
            if (r.AnyPendingEnable) return;   // device is still stopped: nothing live to show
        }
        else if (r.AnyDisabled)
        {
            var box = new Rectangle(x, y, w, D(96));
            FillRound(g, Color.FromArgb(70, 52, 25), box, D(8));
            Text_(g, "Disabled at the driver level", fHead, Warn, box.X + D(14), box.Y + D(12));
            TextRenderer.DrawText(g, "Windows has stopped this device, so games, launchers and other apps cannot detect it (and neither can the live view here).\nClick “Enable” above to bring it back.",
                fNorm, new Rectangle(box.X + D(14), box.Y + D(38), box.Width - D(28), D(56)), Fg, TextFormatFlags.WordBreak);
            return;
        }

        var hid = r.ActiveHid;
        if (hid == null)
        {
            var box = new Rectangle(x, y, w, D(70));
            FillRound(g, Card, box, D(8));
            TextRenderer.DrawText(g, r.XInputStyle
                    ? "This controller has no HID input interface, so it is read through XInput — see the player slots at the bottom."
                    : "No input interface is available yet (the device may still be starting, or has a driver problem).",
                fNorm, Rectangle.Inflate(box, -D(14), -D(10)), Fg, TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter);
            return;
        }

        // stats tiles
        var s = hid.Stats.Get(now);
        var tiles = new (string, string)[]
        {
            ("Report rate", $"{s.Hz:0} Hz"),
            ("Avg interval", s.AvgMs > 0 ? $"{s.AvgMs:0.00} ms" : "—"),
            ("Jitter", s.AvgMs > 0 ? $"±{s.JitterMs:0.00} ms" : "—"),
            ("Worst gap", s.AvgMs > 0 ? $"{s.MaxGapMs:0.0} ms" : "—"),
            ("Last input", FmtAgo(s.SinceLastMs)),
            ("Reports", s.Total.ToString("N0")),
        };
        int perRow = w / tiles.Length >= D(125) ? tiles.Length : 3;
        int gap = D(8), tw = (w - gap * (perRow - 1)) / perRow, th = D(58);
        for (int i = 0; i < tiles.Length; i++)
        {
            var tr = new Rectangle(x + (i % perRow) * (tw + gap), y + (i / perRow) * (th + gap), tw, th);
            FillRound(g, Card, tr, D(8));
            TextIn(g, tiles[i].Item1, fSmall, Dim, new Rectangle(tr.X + D(10), tr.Y + D(6), tr.Width - D(14), D(18)), TextFormatFlags.Left);
            TextIn(g, tiles[i].Item2, fTile, i == 0 && s.Hz > 0 ? Accent : Fg, new Rectangle(tr.X + D(10), tr.Y + D(25), tr.Width - D(14), D(26)), TextFormatFlags.Left);
        }
        y += (tiles.Length + perRow - 1) / perRow * (th + gap);
        string hint = s.Total == 0 ? "Waiting for input — press a button or move a stick."
            : s.Hz < 2 ? "Idle: this pad only sends reports when something changes. Wiggle a stick to measure."
            : "Interval = how often the pad delivers a new state to the PC (local “ping”).";
        TextIn(g, hint, fSmall, Dim, new Rectangle(x, y, w, D(18)), TextFormatFlags.Left); y += D(24);

        if (!hid.ParseOk)
        {
            Text_(g, "Could not parse this device's HID descriptor; showing raw reports only.", fNorm, Warn, x, y); y += D(24);
        }

        // axes (two columns)
        var axes = hid.Values.Where(v => !v.IsHat).ToList();
        var hats = hid.Values.Where(v => v.IsHat).ToList();
        if (axes.Count > 0)
        {
            Text_(g, "AXES", fHead, Dim, x, y); y += D(22);
            int cols = 2, colW = (w - D(16)) / cols, rowH = D(24);
            for (int i = 0; i < axes.Count; i++)
            {
                var v = axes[i];
                int cx = x + (i % cols) * (colW + D(16)), cy = y + (i / cols) * rowH;
                Text_(g, v.Label, fHead, Fg, cx, cy + D(2));
                var bar = new Rectangle(cx + D(56), cy + D(5), colW - D(56) - D(70), D(12));
                FillRound(g, Card2, bar, D(6));
                if (v.Has)
                {
                    int fillW = (int)(bar.Width * v.Normalized);
                    if (fillW > 0) FillRound(g, Color.FromArgb(150, Accent), new Rectangle(bar.X, bar.Y, Math.Max(fillW, D(12)), bar.Height), D(6));
                    using var pen = new Pen(Color.White, D(2));
                    g.DrawLine(pen, bar.X + fillW, bar.Y - D(2), bar.X + fillW, bar.Bottom + D(2));
                }
                Text_(g, v.Has ? v.Raw.ToString() : "—", fMono, Dim, bar.Right + D(10), cy + D(3));
            }
            y += ((axes.Count + cols - 1) / cols) * rowH + D(10);
        }

        // hats + buttons on one band
        int bandY = y;
        int hx = x;
        foreach (var hat in hats)
        {
            if (hx == x) Text_(g, "D-PAD / HAT", fHead, Dim, x, bandY);
            int sz = D(70); var c = new Rectangle(hx, bandY + D(24), sz, sz);
            using (var b = new SolidBrush(Card)) g.FillEllipse(b, c);
            var ang = hat.HatAngle;
            for (int k = 0; k < 8; k++)
            {
                double a = (k * 45 - 90) * Math.PI / 180;
                float px = c.X + sz / 2f + (float)Math.Cos(a) * sz * 0.36f, py = c.Y + sz / 2f + (float)Math.Sin(a) * sz * 0.36f;
                bool on = ang.HasValue && Math.Abs(((ang.Value - k * 45) + 540) % 360 - 180) < 1;
                using var b = new SolidBrush(on ? Good : Card2);
                float rr = on ? D(7) : D(4);
                g.FillEllipse(b, px - rr, py - rr, rr * 2, rr * 2);
            }
            TextIn(g, ang.HasValue ? $"{ang:0}°" : "·", fSmall, Dim, c);
            hx += sz + D(20);
        }

        int bx0 = hats.Count > 0 ? hx + D(10) : x;
        if (hid.ButtonRanges.Count > 0)
        {
            Text_(g, $"BUTTONS  ({hid.Pressed.Count} pressed)", fHead, Dim, bx0, bandY);
            int bs = D(32), bg = D(6), bx = bx0, by = bandY + D(24), maxX = x + w;
            foreach (var (page, min, max) in hid.ButtonRanges)
            {
                for (int u = min; u <= max; u++)
                {
                    if (bx + bs > maxX) { bx = bx0; by += bs + bg; }
                    bool on = hid.Pressed.Contains(((uint)page << 16) | (uint)u);
                    var br = new Rectangle(bx, by, bs, bs);
                    using (var b = new SolidBrush(on ? Good : Card)) g.FillEllipse(b, br);
                    string lbl = page == 9 ? u.ToString() : $"{u:X2}";
                    TextIn(g, lbl, fBtn, on ? Color.Black : Fg, br);
                    bx += bs + bg;
                }
            }
            y = Math.Max(by + bs, bandY + D(24) + (hats.Count > 0 ? D(70) : 0)) + D(16);
        }
        else if (hats.Count > 0) y = bandY + D(24) + D(70) + D(16);

        // raw report
        Text_(g, "LAST RAW REPORT", fHead, Dim, x, y); y += D(20);
        var raw = hid.LastReport;
        string hex = raw == null ? "(none yet)" : BitConverter.ToString(raw).Replace('-', ' ');
        TextRenderer.DrawText(g, hex, fMono, new Rectangle(x, y, w, devView.ClientSize.Height - y - D(6)), Dim, TextFormatFlags.WordBreak);
    }

    // ───────────── bottom strip: live inputs for every controller ─────────────
    // Each rumble button maps to an action: on(true) starts the motors, on(false) stops them; returns an error or null.
    readonly List<(Rectangle Rect, Func<bool, string> On)> rumbleHits = new();
    readonly List<Func<bool, string>> activeRumbles = new();
    readonly HashSet<string> btRumbleConsent = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(Rectangle Rect, string Key)> cardHits = new();

    void PaintInputs(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Bg);
        rumbleHits.Clear(); cardHits.Clear();
        int W = xView.ClientSize.Width, H = xView.ClientSize.Height, pad = D(12);
        Text_(g, "LIVE INPUTS", fHead, Dim, pad, D(8));
        TextIn(g, "Every connected controller. Xbox-style pads are read through XInput, others (DirectInput) from their HID reports. Rumble works on Xbox-style and PlayStation pads. Click a card to select it.",
            fSmall, Dim, new Rectangle(pad + D(100), D(8), W - pad * 2 - D(100), D(18)), TextFormatFlags.Left);

        long now = Stopwatch.GetTimestamp();
        var cards = rows.Select(r => (Row: r, Slot: SlotOf(r))).ToList();
        for (int i = 0; i < 4; i++)
        {
            bool c; lock (xinput.Slots[i]) c = xinput.Slots[i].Connected;
            if (c && slotKey[i] == null) cards.Add((null, i));   // XInput pad not tied to a listed controller yet
        }
        if (cards.Count == 0) { TextIn(g, "No controllers connected.", fHead, Dim, new Rectangle(0, D(32), W, H - D(32))); return; }

        int gap = D(10), n = Math.Max(cards.Count, 3), cw = (W - pad * 2 - gap * (n - 1)) / n, ch = H - D(40), cy = D(32);
        var sel = SelectedRow;
        for (int k = 0; k < cards.Count; k++)
        {
            var (row, slot) = cards[k];
            var card = new Rectangle(pad + k * (cw + gap), cy, cw, ch);
            FillRound(g, Card, card, D(10));
            if (row != null && row == sel)
                using (var p = new Pen(Color.FromArgb(150, Accent), D(2))) using (var path = Round(card, D(10))) g.DrawPath(p, path);
            if (row != null) cardHits.Add((card, row.Key));

            int x = card.X + D(12), y = card.Y + D(10), iw = card.Width - D(24);
            TextIn(g, row?.Name ?? $"XInput Player {slot + 1}", fHead, Fg, new Rectangle(x, y, iw, D(20)), TextFormatFlags.Left);
            y += D(22);
            string tag = row == null ? "unidentified · move a stick to match it"
                : !row.XInputStyle ? $"{row.Connection} · DirectInput (HID)"
                : slot >= 0 ? $"{row.Connection} · XInput Player {slot + 1}"
                : $"{row.Connection} · XInput (slot unknown)";
            var inner = new Rectangle(x, y, iw, card.Bottom - D(10) - y);

            if (row != null && (row.AnyDisabled || row.AnyPendingEnable))
            {
                TextIn(g, tag, fSmall, Dim, new Rectangle(x, y, iw, D(18)), TextFormatFlags.Left);
                TextIn(g, "Disabled — invisible to games", fNorm, Warn, new Rectangle(x, y + D(20), iw, inner.Height - D(20)));
            }
            else if (slot >= 0) DrawXInputCard(g, inner, slot, tag, now);
            else if (row?.ActiveHid is HidDevice h) DrawHidCard(g, inner, h, tag, now);
            else
            {
                TextIn(g, tag, fSmall, Dim, new Rectangle(x, y, iw, D(18)), TextFormatFlags.Left);
                TextIn(g, "No live input available", fNorm, Dim, new Rectangle(x, y + D(20), iw, inner.Height - D(20)));
            }
        }
    }

    void DrawXInputCard(Graphics g, Rectangle a, int slot, string tag, long now)
    {
        var s = xinput.Slots[slot];
        Native.XINPUT_STATE st; RateSnapshot rs; byte bt, bl;
        lock (s) { st = s.State; rs = s.Stats.Get(now); bt = s.BatteryType; bl = s.BatteryLevel; }
        string bat = bt switch
        {
            1 => " · wired",
            2 or 3 => " · battery " + (bl switch { 0 => "empty", 1 => "low", 2 => "medium", _ => "full" }),
            _ => "",
        };
        TextIn(g, tag + bat + (rs.Hz > 0 ? $" · {rs.Hz:0} Hz · {rs.AvgMs:0.0} ms" : " · idle"), fSmall, Dim,
            new Rectangle(a.X, a.Y, a.Width, D(18)), TextFormatFlags.Left);
        int y = a.Y + D(24);

        var gp = st.Gamepad;
        int stick = Math.Min(D(64), (a.Width - D(56)) / 2);
        DrawStick(g, new Rectangle(a.X, y, stick, stick), gp.sThumbLX, gp.sThumbLY, (gp.wButtons & 0x40) != 0);
        DrawStick(g, new Rectangle(a.Right - stick, y, stick, stick), gp.sThumbRX, gp.sThumbRY, (gp.wButtons & 0x80) != 0);
        int mid = a.X + a.Width / 2, tbw = D(12);
        DrawTrigger(g, new Rectangle(mid - tbw - D(5), y, tbw, stick), gp.bLeftTrigger);
        DrawTrigger(g, new Rectangle(mid + D(5), y, tbw, stick), gp.bRightTrigger);
        y += stick + D(10);

        var btns = new (string, bool)[]
        {
            ("A", (gp.wButtons & 0x1000) != 0), ("B", (gp.wButtons & 0x2000) != 0), ("X", (gp.wButtons & 0x4000) != 0), ("Y", (gp.wButtons & 0x8000) != 0),
            ("LB", (gp.wButtons & 0x100) != 0), ("RB", (gp.wButtons & 0x200) != 0),
            ("↑", (gp.wButtons & 1) != 0), ("↓", (gp.wButtons & 2) != 0), ("←", (gp.wButtons & 4) != 0), ("→", (gp.wButtons & 8) != 0),
            ("Back", (gp.wButtons & 0x20) != 0), ("Start", (gp.wButtons & 0x10) != 0), ("Guide", (gp.wButtons & 0x400) != 0),
        };
        y = DrawChips(g, a, y, btns, a.Bottom - D(32));

        var rr = new Rectangle(a.X, Math.Min(y + D(4), a.Bottom - D(26)), a.Width, D(26));
        rumbleHits.Add((rr, on => { xinput.Rumble(slot, on); return null; }));
        bool hold = rr.Contains(xView.PointToClient(Cursor.Position)) && (MouseButtons & MouseButtons.Left) != 0;
        FillRound(g, hold ? Accent : Card2, rr, D(6));
        TextIn(g, hold ? "Rumbling…" : "Hold to rumble", fBtn, hold ? Color.Black : Fg, rr);
    }

    void DrawHidCard(Graphics g, Rectangle a, HidDevice h, string tag, long now)
    {
        var rs = h.Stats.Get(now);
        TextIn(g, tag + (rs.Hz > 0 ? $" · {rs.Hz:0} Hz · {rs.AvgMs:0.0} ms" : " · idle"), fSmall, Dim,
            new Rectangle(a.X, a.Y, a.Width, D(18)), TextFormatFlags.Left);
        int y = a.Y + D(24);

        ValueInfo V(int u) => h.Values.FirstOrDefault(v => v.Page == 1 && v.Usage == u && v.Has);
        ValueInfo X = V(0x30), Y = V(0x31), Z = V(0x32), Rx = V(0x33), Ry = V(0x34), Rz = V(0x35);
        // Most pads put the right stick on Z/Rz (triggers on Rx/Ry); Xbox-style HID uses Rx/Ry (triggers on Z).
        ValueInfo rsx, rsy; var trig = new List<ValueInfo>();
        if (Z != null && Rz != null) { rsx = Z; rsy = Rz; trig.AddRange(new[] { Rx, Ry }.Where(v => v != null)); }
        else { rsx = Rx; rsy = Ry; if (Z != null) trig.Add(Z); }
        foreach (var v in h.Values.Where(v => v.Has && v.Page == 2)) trig.Add(v);   // accelerator / brake etc.

        int stick = Math.Min(D(64), (a.Width - D(56)) / 2);
        DrawStickN(g, new Rectangle(a.X, y, stick, stick), X, Y);
        DrawStickN(g, new Rectangle(a.Right - stick, y, stick, stick), rsx, rsy);
        var shown = trig.Take(4).ToList();
        int mid = a.X + a.Width / 2, tbw = D(12), tg = D(10);
        int tx = mid - (shown.Count * tbw + Math.Max(0, shown.Count - 1) * tg) / 2;
        foreach (var t in shown) { DrawTrigger(g, new Rectangle(tx, y, tbw, stick), (byte)(t.Normalized * 255)); tx += tbw + tg; }
        y += stick + D(10);

        var chips = new List<(string, bool)>();
        var hat = h.Values.FirstOrDefault(v => v.IsHat && v.Has);
        if (hat != null)
        {
            var ang = hat.HatAngle;
            bool Near(double t) => ang.HasValue && Math.Abs(((ang.Value - t) + 540) % 360 - 180) <= 46;
            chips.Add(("↑", Near(0))); chips.Add(("↓", Near(180))); chips.Add(("←", Near(270))); chips.Add(("→", Near(90)));
        }
        foreach (var (page, min, max) in h.ButtonRanges)
            for (int u = min; u <= max; u++)
                chips.Add((page == 9 ? u.ToString() : $"{u:X2}", h.Pressed.Contains(((uint)page << 16) | (uint)u)));
        y = DrawChips(g, a, y, chips, a.Bottom - D(32));

        var rr = new Rectangle(a.X, Math.Min(y + D(4), a.Bottom - D(26)), a.Width, D(26));
        if (h.CanRumble)
        {
            rumbleHits.Add((rr, on =>
            {
                if (on && h.IsBluetooth && !btRumbleConsent.Contains(h.InstanceId ?? ""))
                {
                    var ok = MessageBox.Show(this,
                        "Rumble over Bluetooth switches this PlayStation controller into its \"extended\" mode.\n\n" +
                        "Until you turn the controller off and on again, games that read it through plain DirectInput may stop seeing " +
                        "its buttons. Steam, DS4Windows and this app keep working.\n\nTurn on rumble anyway?",
                        "Rumble over Bluetooth", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                    if (ok != DialogResult.OK) return "";
                    btRumbleConsent.Add(h.InstanceId ?? "");
                    return "";   // the dialog swallowed the mouse press; next press rumbles
                }
                return h.Rumble((byte)(on ? 200 : 0), (byte)(on ? 200 : 0));
            }));
            bool hold = rr.Contains(xView.PointToClient(Cursor.Position)) && (MouseButtons & MouseButtons.Left) != 0;
            FillRound(g, hold ? Accent : Card2, rr, D(6));
            TextIn(g, hold ? "Rumbling…" : "Hold to rumble", fBtn, hold ? Color.Black : Fg, rr);
        }
        else
        {
            FillRound(g, Color.FromArgb(48, 50, 55), rr, D(6));
            TextIn(g, "Rumble not supported for this controller", fBtn, Dim, rr);
        }
    }

    /// Lays out labelled button chips left-to-right, wrapping; returns the y below the last row.
    int DrawChips(Graphics g, Rectangle a, int y, IEnumerable<(string Label, bool On)> chips, int maxBottom)
    {
        int bx = a.X, bh = D(20);
        foreach (var (label, on) in chips)
        {
            int bw = Math.Max(D(22), TextRenderer.MeasureText(label, fBtn).Width + D(8));
            if (bx + bw > a.Right) { bx = a.X; y += bh + D(4); }
            if (y + bh > maxBottom) break;
            var br = new Rectangle(bx, y, bw, bh);
            FillRound(g, on ? Good : Card2, br, D(5));
            TextIn(g, label, fBtn, on ? Color.Black : Fg, br);
            bx += bw + D(4);
        }
        return y + bh;
    }

    void DrawStickN(Graphics g, Rectangle r, ValueInfo vx, ValueInfo vy)
    {
        if (vx == null || vy == null)
        {
            using var b = new SolidBrush(Card2); g.FillEllipse(b, r);
            TextIn(g, "—", fSmall, Dim, r);
            return;
        }
        static short S(double n) => (short)Math.Clamp((n * 2 - 1) * 32767, -32767, 32767);
        DrawStick(g, r, S(vx.Normalized), (short)-S(vy.Normalized), false);   // HID Y grows downward
    }

    void DrawStick(Graphics g, Rectangle r, short sx, short sy, bool pressed)
    {
        using (var b = new SolidBrush(pressed ? Color.FromArgb(40, 90, 60) : Card2)) g.FillEllipse(b, r);
        using (var p = new Pen(Color.FromArgb(80, 84, 92), 1))
        {
            g.DrawLine(p, r.X + r.Width / 2, r.Y + 4, r.X + r.Width / 2, r.Bottom - 4);
            g.DrawLine(p, r.X + 4, r.Y + r.Height / 2, r.Right - 4, r.Y + r.Height / 2);
            float dz = r.Width * (7849f / 32768f) / 2;   // XInput's recommended left-stick deadzone
            g.DrawEllipse(p, r.X + r.Width / 2f - dz, r.Y + r.Height / 2f - dz, dz * 2, dz * 2);
        }
        float px = r.X + r.Width / 2f + sx / 32768f * (r.Width / 2f - D(6));
        float py = r.Y + r.Height / 2f - sy / 32768f * (r.Height / 2f - D(6));
        float d = D(6);
        using var dot = new SolidBrush(pressed ? Good : Accent);
        g.FillEllipse(dot, px - d, py - d, d * 2, d * 2);
    }

    void DrawTrigger(Graphics g, Rectangle r, byte v)
    {
        FillRound(g, Card2, r, D(4));
        int h = (int)(r.Height * v / 255f);
        if (h > 0) FillRound(g, Accent, new Rectangle(r.X, r.Bottom - h, r.Width, h), D(4));
    }
}
