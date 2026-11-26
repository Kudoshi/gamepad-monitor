using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace GamepadMonitor;

// ───────────────────────────── Native imports ─────────────────────────────
static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid; public uint pid;
        public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; }
    }

    public static readonly DEVPROPKEY PKEY_DeviceDesc      = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    public static readonly DEVPROPKEY PKEY_HardwareIds     = new("a45c254e-df1c-4efd-8020-67d146a850e0", 3);
    public static readonly DEVPROPKEY PKEY_CompatibleIds   = new("a45c254e-df1c-4efd-8020-67d146a850e0", 4);
    public static readonly DEVPROPKEY PKEY_Class           = new("a45c254e-df1c-4efd-8020-67d146a850e0", 9);
    public static readonly DEVPROPKEY PKEY_FriendlyName    = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    public static readonly DEVPROPKEY PKEY_ConfigFlags     = new("a45c254e-df1c-4efd-8020-67d146a850e0", 12);
    public static readonly DEVPROPKEY PKEY_Parent          = new("4340a6c5-93fa-4706-972c-7b648008a5a7", 8);
    public static readonly DEVPROPKEY PKEY_BusReportedDesc = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);

    public const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    public const uint DN_HAS_PROBLEM = 0x400;
    public const uint CM_PROB_DISABLED = 22;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_ID_ListW(string filter, char[] buffer, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[] buf, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    // Raw input
    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    public const uint RIDEV_INPUTSINK = 0x100, RIDEV_DEVNOTIFY = 0x2000;
    public const uint RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000b, RIDI_PREPARSEDDATA = 0x20000005;
    public const uint RID_INPUT = 0x10000003, RIM_TYPEHID = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devs, uint num, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint num, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetRawInputDeviceInfoW(IntPtr h, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint headerSize);

    // HID parser
    public const int HIDP_STATUS_SUCCESS = 0x00110000;
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("hid.dll")] public static extern int HidP_GetButtonCaps(int type, byte[] caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetUsages(int type, ushort page, ushort link, ushort[] list, ref uint len, IntPtr pp, byte[] report, uint reportLen);
    [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pp, byte[] report, uint reportLen);

    // XInput
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed, wRightMotorSpeed; }
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_BATTERY_INFORMATION { public byte BatteryType, BatteryLevel; }

    [DllImport("xinput1_4.dll")] public static extern uint XInputGetState(uint idx, out XINPUT_STATE state);
    [DllImport("xinput1_4.dll")] public static extern uint XInputSetState(uint idx, ref XINPUT_VIBRATION vib);
    [DllImport("xinput1_4.dll")] public static extern uint XInputGetBatteryInformation(uint idx, byte devType, out XINPUT_BATTERY_INFORMATION info);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint XInputGetStateFn(uint idx, out XINPUT_STATE state);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr LoadLibraryW(string name);
    [DllImport("kernel32.dll")] public static extern IntPtr GetProcAddress(IntPtr mod, IntPtr ordinal);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, uint len, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, uint len, out uint read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(Microsoft.Win32.SafeHandles.SafeFileHandle h, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[] inBuf, uint inLen, byte[] outBuf, uint outLen, out uint returned, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID, ProductID, VersionNumber; }
    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetAttributes(Microsoft.Win32.SafeHandles.SafeFileHandle h, ref HIDD_ATTRIBUTES attr);
    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetPreparsedData(Microsoft.Win32.SafeHandles.SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")]
    public static extern bool HidD_FreePreparsedData(IntPtr pp);

    public static readonly Guid GUID_DEVINTERFACE_HID = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    public static readonly Guid GUID_DEVINTERFACE_USB_HUB = new("F18A0E88-C30C-11D0-8815-00A0C906BED8");
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid cls, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid cls, string deviceId, char[] buf, uint len, uint flags);

    public static List<string> InterfacesOf(Guid cls, string deviceId)
    {
        if (CM_Get_Device_Interface_List_SizeW(out uint len, ref cls, deviceId, 0) != 0 || len <= 1) return new();
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref cls, deviceId, buf, len, 0) != 0) return new();
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetOutputReport(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, uint len);

    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
}

// ───────────────────────────── Timing ("ping") ─────────────────────────────
public struct RateSnapshot
{
    public double Hz, AvgMs, JitterMs, MaxGapMs, SinceLastMs;
    public long Total;
}

/// Tracks the arrival time of every input report over a sliding 1-second window.
sealed class RateStats
{
    static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
    readonly List<long> stamps = new();
    public long Total, Last;

    public void Hit(long now)
    {
        Total++; Last = now; stamps.Add(now); Trim(now);
    }

    void Trim(long now)
    {
        long cut = now - Stopwatch.Frequency;
        int i = 0;
        while (i < stamps.Count && stamps[i] < cut) i++;
        if (i > 0) stamps.RemoveRange(0, i);
    }

    public RateSnapshot Get(long now)
    {
        Trim(now);
        var s = new RateSnapshot { Total = Total, SinceLastMs = Last == 0 ? -1 : (now - Last) * TickMs, Hz = stamps.Count };
        if (stamps.Count >= 2)
        {
            int n = stamps.Count - 1;
            double sum = 0, max = 0;
            var d = new double[n];
            for (int i = 0; i < n; i++) { d[i] = (stamps[i + 1] - stamps[i]) * TickMs; sum += d[i]; if (d[i] > max) max = d[i]; }
            double mean = sum / n, var = 0;
            for (int i = 0; i < n; i++) var += (d[i] - mean) * (d[i] - mean);
            s.AvgMs = mean; s.JitterMs = Math.Sqrt(var / n); s.MaxGapMs = max;
        }
        return s;
    }
}

// ───────────────────────────── PnP device tree ─────────────────────────────
sealed class DevNode
{
    public string Id, Class, Parent, Friendly, Desc, BusDesc;
    public string[] HwIds = Array.Empty<string>(), CompatIds = Array.Empty<string>();
    public uint Inst, Status, Problem, ConfigFlags;
    public bool Disabled => (Status & Native.DN_HAS_PROBLEM) != 0 && Problem == Native.CM_PROB_DISABLED;
    /// Marked disabled (CONFIGFLAG_DISABLED) but still running: a program refused to release it.
    public bool PendingDisable => (ConfigFlags & 1) != 0 && !Disabled;
    /// Marked enabled again but still stopped until restart/replug.
    public bool PendingEnable => (ConfigFlags & 1) == 0 && Disabled;
}

sealed class GamepadRow
{
    public string Key, Name, Connection, Vid, Pid;
    public DevNode Top;
    public List<DevNode> Members = new();
    public List<HidDevice> Hids = new();
    public bool XInputStyle;
    /// On HidHide's hide list: invisible to games and other apps, still visible to this app.
    public bool Hidden;

    public bool AnyDisabled => Members.Any(m => m.Disabled && !m.PendingEnable);
    public bool AnyPendingDisable => Members.Any(m => m.PendingDisable);
    public bool AnyPendingEnable => Members.Any(m => m.PendingEnable);
    /// True when the Enable button should be offered (disabled, or a disable is still waiting).
    public bool WantsEnable => AnyDisabled || AnyPendingDisable;
    public HidDevice ActiveHid => Hids.OrderByDescending(h => h.Stats.Last).FirstOrDefault();

    public string StateText
    {
        get
        {
            if (AnyPendingDisable) return "Disable pending";
            if (AnyPendingEnable) return "Enable pending";
            if (AnyDisabled) return "Disabled";
            if (Hidden) return "Hidden";
            if ((Top.Status & Native.DN_HAS_PROBLEM) != 0) return "Problem " + Top.Problem;
            if (Hids.Count == 0) return XInputStyle ? "Enabled (XInput)" : "No input";
            return "Enabled";
        }
    }
}

static class DeviceScanner
{
    static readonly HashSet<string> GameHwIds = new(StringComparer.OrdinalIgnoreCase)
        { "HID_DEVICE_SYSTEM_GAME", "HID_DEVICE_UP:0001_U:0004", "HID_DEVICE_UP:0001_U:0005" };

    // Xbox 360 (XUSB wired / wireless receiver) and Xbox One/Series (GIP) USB functions
    static readonly HashSet<string> XboxCompat = new(StringComparer.OrdinalIgnoreCase)
    {
        @"USB\Class_FF&SubClass_5D&Prot_01", @"USB\Class_FF&SubClass_5D&Prot_81",
        @"USB\Class_FF&SubClass_47&Prot_D0", @"USB\MS_COMP_XUSB10", @"USB\MS_COMP_XGIP10",
    };
    static readonly HashSet<string> XboxClasses = new(StringComparer.OrdinalIgnoreCase) { "XnaComposite", "XboxComposite" };

    static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["054C:0268"] = "DualShock 3", ["054C:05C4"] = "DualShock 4", ["054C:09CC"] = "DualShock 4 (v2)",
        ["054C:0BA0"] = "DualShock 4 USB Wireless Adapter", ["054C:0CE6"] = "DualSense", ["054C:0DF2"] = "DualSense Edge",
        ["045E:028E"] = "Xbox 360 Controller", ["045E:0719"] = "Xbox 360 Wireless Receiver",
        ["045E:02DD"] = "Xbox One Controller", ["045E:02EA"] = "Xbox One S Controller", ["045E:02FF"] = "Xbox One/Series Controller",
        ["045E:0B12"] = "Xbox Series X|S Controller", ["045E:0B13"] = "Xbox Series X|S Controller",
        ["057E:2006"] = "Joy-Con (L)", ["057E:2007"] = "Joy-Con (R)", ["057E:2009"] = "Switch Pro Controller",
    };

    static string GetString(uint inst, Native.DEVPROPKEY key)
    {
        var b = GetBytes(inst, key);
        return b == null ? null : Encoding.Unicode.GetString(b).TrimEnd('\0');
    }

    static string[] GetMulti(uint inst, Native.DEVPROPKEY key)
    {
        var b = GetBytes(inst, key);
        if (b == null) return Array.Empty<string>();
        return Encoding.Unicode.GetString(b).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    static byte[] GetBytes(uint inst, Native.DEVPROPKEY key)
    {
        uint size = 0;
        Native.CM_Get_DevNode_PropertyW(inst, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        return Native.CM_Get_DevNode_PropertyW(inst, ref key, out _, buf, ref size, 0) == 0 ? buf : null;
    }

    static List<string> PresentIds()
    {
        Native.CM_Get_Device_ID_List_SizeW(out uint len, null, Native.CM_GETIDLIST_FILTER_PRESENT);
        var buf = new char[len];
        if (Native.CM_Get_Device_ID_ListW(null, buf, len, Native.CM_GETIDLIST_FILTER_PRESENT) != 0) return new();
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    static DevNode LoadLight(string id)
    {
        if (Native.CM_Locate_DevNodeW(out uint inst, id, 0) != 0) return null;
        var n = new DevNode
        {
            Id = id, Inst = inst,
            HwIds = GetMulti(inst, Native.PKEY_HardwareIds),
            CompatIds = GetMulti(inst, Native.PKEY_CompatibleIds),
            Class = GetString(inst, Native.PKEY_Class),
            Parent = GetString(inst, Native.PKEY_Parent),
        };
        Native.CM_Get_DevNode_Status(out n.Status, out n.Problem, inst, 0);
        var cf = GetBytes(inst, Native.PKEY_ConfigFlags);
        if (cf != null && cf.Length >= 4) n.ConfigFlags = BitConverter.ToUInt32(cf, 0);
        return n;
    }

    static void LoadDetails(DevNode n)
    {
        if (n.Desc != null) return;
        n.Friendly = GetString(n.Inst, Native.PKEY_FriendlyName);
        n.Desc = GetString(n.Inst, Native.PKEY_DeviceDesc) ?? "";
        n.BusDesc = GetString(n.Inst, Native.PKEY_BusReportedDesc);
    }

    static bool IsGamepad(DevNode n) =>
        n.HwIds.Any(GameHwIds.Contains) || n.CompatIds.Any(XboxCompat.Contains) || (n.Class != null && XboxClasses.Contains(n.Class));

    static bool IsXboxFunction(DevNode n) =>
        n.CompatIds.Any(XboxCompat.Contains) || (n.Class != null && XboxClasses.Contains(n.Class));

    public static List<GamepadRow> Scan(ICollection<string> extraHidInstanceIds)
    {
        var nodes = new Dictionary<string, DevNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in PresentIds())
        {
            var n = LoadLight(id);
            if (n != null) nodes[id] = n;
        }

        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes.Values) if (IsGamepad(n)) matched.Add(n.Id);
        foreach (var id in extraHidInstanceIds) if (nodes.ContainsKey(id)) matched.Add(id);

        List<DevNode> Chain(DevNode n)
        {
            var list = new List<DevNode>();
            var cur = n;
            for (int guard = 0; cur != null && guard < 32; guard++)
            {
                list.Add(cur);
                if (string.IsNullOrEmpty(cur.Parent)) break;
                nodes.TryGetValue(cur.Parent, out cur);
            }
            return list;
        }

        // Group each matched node under its top-most matched ancestor
        // (e.g. Xbox 360 USB function -> its HID child), so one physical pad = one row.
        var groups = new Dictionary<string, List<DevNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in matched)
        {
            var chain = Chain(nodes[id]);
            var top = chain.Last(c => matched.Contains(c.Id));
            if (!groups.TryGetValue(top.Id, out var g)) groups[top.Id] = g = new List<DevNode>();
            g.Add(nodes[id]);
        }

        var rows = new List<GamepadRow>();
        foreach (var (topId, members) in groups)
        {
            var top = nodes[topId];
            var chain = Chain(top);
            foreach (var c in chain.Take(4)) LoadDetails(c);
            foreach (var m in members) LoadDetails(m);

            var row = new GamepadRow { Key = topId, Top = top, Members = members.OrderBy(m => m == top ? 0 : 1).ToList() };
            row.XInputStyle = members.Any(IsXboxFunction);
            row.Connection = ClassifyConnection(chain);
            (row.Vid, row.Pid) = ParseVidPid(members.Select(m => m.Id).Prepend(topId));

            string name = null;
            if (row.Vid != null && KnownNames.TryGetValue(row.Vid + ":" + row.Pid, out var known)) name = known;
            name ??= chain.Take(4).Select(c => c.BusDesc).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("USB Input", StringComparison.OrdinalIgnoreCase));
            name ??= top.Friendly ?? top.Desc;
            if (string.IsNullOrWhiteSpace(name)) name = topId;
            if (row.Connection == "Virtual") name += " (virtual)";
            row.Name = name;
            rows.Add(row);
        }
        return rows.OrderBy(r => r.Name).ThenBy(r => r.Key).ToList();
    }

    static string ClassifyConnection(List<DevNode> chain)
    {
        // Walk upward from the device; the first bus we meet decides. (Every tree ends at ROOT\ACPI_HAL,
        // so ROOT\ only means "virtual" when it is reached before any real USB hub / PCI controller.)
        bool sawUsb = false;
        foreach (var c in chain)
        {
            var i = c.Id.ToUpperInvariant();
            if (i.StartsWith(@"BTHLEDEVICE\") || i.StartsWith(@"BTHLE\")) return "Bluetooth LE";
            if (i.StartsWith(@"BTHENUM\") || i.StartsWith(@"BTH\") || i.Contains("{00001124-0000-1000-8000-00805F9B34FB}")) return "Bluetooth";
            if (i.Contains("VID_045E&PID_0719")) return "Xbox 360 Wireless";
            if (i.Contains("VID_045E&PID_02E6") || i.Contains("VID_045E&PID_02FE")) return "Xbox Wireless";
            if (i.StartsWith(@"USB\ROOT_HUB") || i.StartsWith(@"PCI\") || i.StartsWith(@"ACPI\")) return sawUsb ? "USB" : c.Id.Split('\\')[0];
            if (i.StartsWith(@"ROOT\") || i.StartsWith(@"SWD\") || i.Contains("VIGEM")) return "Virtual";
            if (i.StartsWith(@"USB\")) sawUsb = true;
        }
        return sawUsb ? "USB" : (chain.Count > 0 ? chain[0].Id.Split('\\')[0] : "?");
    }

    static readonly Regex VidRx = new(@"VID[_&](?:0002|0001)?([0-9A-F]{4})", RegexOptions.IgnoreCase);
    static readonly Regex PidRx = new(@"PID[_&]([0-9A-F]{4})", RegexOptions.IgnoreCase);

    static (string, string) ParseVidPid(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            var v = VidRx.Match(id); var p = PidRx.Match(id);
            if (v.Success && p.Success) return (v.Groups[1].Value.ToUpperInvariant(), p.Groups[1].Value.ToUpperInvariant());
        }
        return (null, null);
    }
}

// ───────────────────────────── Raw input / HID ─────────────────────────────
sealed class ValueInfo
{
    public ushort Page, Usage, Bits;
    public int LMin, LMax, Raw;
    public bool Has;
    public bool IsHat => Page == 1 && Usage == 0x39;

    public double Normalized
    {
        get
        {
            long min = LMin, max = LMax;
            if (max <= min) { min = 0; max = Bits >= 31 ? int.MaxValue : (1L << Bits) - 1; }
            return Math.Clamp((Raw - min) / (double)(max - min), 0, 1);
        }
    }

    /// Degrees clockwise from up, or null when centered.
    public double? HatAngle
    {
        get
        {
            if (!Has || Raw < LMin || Raw > LMax || LMax <= LMin) return null;
            return (Raw - LMin) * 360.0 / (LMax - LMin + 1);
        }
    }

    public string Label => (Page, Usage) switch
    {
        (1, 0x30) => "X", (1, 0x31) => "Y", (1, 0x32) => "Z",
        (1, 0x33) => "Rx", (1, 0x34) => "Ry", (1, 0x35) => "Rz",
        (1, 0x36) => "Slider", (1, 0x37) => "Dial", (1, 0x38) => "Wheel", (1, 0x39) => "Hat",
        (2, 0xBA) => "Rudder", (2, 0xBB) => "Throttle", (2, 0xC4) => "Accel", (2, 0xC5) => "Brake", (2, 0xC8) => "Steering",
        _ => $"{Page:X2}:{Usage:X2}",
    };
}

enum SonyKind { None, DualShock4, DualSense }

sealed class HidDevice : IDisposable
{
    public IntPtr Handle, Preparsed;
    public ushort Vid, Pid;
    public int OutputLen, InputLen;
    /// True when this app reads the device itself (used while HidHide hides it from Raw Input).
    public bool Direct;
    Microsoft.Win32.SafeHandles.SafeFileHandle readHandle;
    Thread readThread;
    volatile bool reading;

    static SonyKind SonyOf(ushort vid, ushort pid) =>
        vid != 0x054C ? SonyKind.None
        : pid is 0x05C4 or 0x09CC or 0x0BA0 ? SonyKind.DualShock4
        : pid is 0x0CE6 or 0x0DF2 ? SonyKind.DualSense
        : SonyKind.None;

    /// Opens a HID gamepad by its device-interface path and reads it on a background thread.
    /// onReport is called from that thread with (device, report, timestamp).
    public static HidDevice OpenDirect(string interfacePath, string instanceId, Action<HidDevice, byte[], long> onReport)
    {
        var h = Native.CreateFileW(interfacePath, 0xC0000000 /*GENERIC_READ|WRITE*/, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); h = Native.CreateFileW(interfacePath, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); }
        if (h.IsInvalid) { h.Dispose(); return null; }
        if (!Native.HidD_GetPreparsedData(h, out var pp)) { h.Dispose(); return null; }
        var attr = new Native.HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
        Native.HidD_GetAttributes(h, ref attr);

        var dev = new HidDevice
        {
            InterfacePath = interfacePath, InstanceId = instanceId, Preparsed = pp, Direct = true, readHandle = h,
            Vid = attr.VendorID, Pid = attr.ProductID,
        };
        dev.Sony = SonyOf(dev.Vid, dev.Pid);
        dev.ParseCapsCore();
        if (dev.UsagePage != 1 || (dev.Usage != 4 && dev.Usage != 5) || dev.InputLen == 0) { dev.Dispose(); return null; }

        dev.reading = true;
        dev.readThread = new Thread(() =>
        {
            var buf = new byte[dev.InputLen];
            while (dev.reading)
            {
                if (!Native.ReadFile(h, buf, (uint)buf.Length, out uint n, IntPtr.Zero) || n == 0) break;
                long now = Stopwatch.GetTimestamp();
                var copy = new byte[n];
                Array.Copy(buf, copy, n);
                onReport(dev, copy, now);
            }
        }) { IsBackground = true, Name = "HID reader" };
        dev.readThread.Start();
        return dev;
    }

    /// The HID device-interface paths for a device instance (e.g. a hidden controller's HID node).
    public static List<string> InterfacesOf(string instanceId) => Native.InterfacesOf(Native.GUID_DEVINTERFACE_HID, instanceId);
    public SonyKind Sony;
    public bool IsBluetooth => InstanceId != null &&
        (InstanceId.Contains("{00001124-0000-1000-8000-00805F9B34FB}", StringComparison.OrdinalIgnoreCase) ||
         InstanceId.Contains("BTHLE", StringComparison.OrdinalIgnoreCase));
    public bool CanRumble => Sony != SonyKind.None;
    Microsoft.Win32.SafeHandles.SafeFileHandle writeHandle;
    byte btSeq;
    public string InterfacePath, InstanceId;
    public ushort UsagePage, Usage;
    public bool ParseOk;
    public readonly List<(ushort Page, ushort Min, ushort Max)> ButtonRanges = new();
    public readonly List<ValueInfo> Values = new();
    public readonly HashSet<uint> Pressed = new();
    public byte[] LastReport;
    public readonly RateStats Stats = new();
    ushort[] buttonPages = Array.Empty<ushort>();
    readonly ushort[] usageBuf = new ushort[512];

    public static string InstanceFromInterface(string path)
    {
        var s = path;
        if (s.StartsWith(@"\\?\") || s.StartsWith(@"\??\")) s = s.Substring(4);
        int k = s.LastIndexOf('#');
        if (k > 0 && k + 1 < s.Length && s[k + 1] == '{') s = s.Substring(0, k);
        return s.Replace('#', '\\');
    }

    public static List<HidDevice> EnumerateGamepads(Dictionary<IntPtr, HidDevice> existing)
    {
        var result = new List<HidDevice>();
        uint n = 0, sz = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
        Native.GetRawInputDeviceList(null, ref n, sz);
        if (n == 0) return result;
        var arr = new Native.RAWINPUTDEVICELIST[n];
        uint got = Native.GetRawInputDeviceList(arr, ref n, sz);
        if (got == uint.MaxValue) return result;

        for (int i = 0; i < got; i++)
        {
            if (arr[i].dwType != Native.RIM_TYPEHID) continue;
            var h = arr[i].hDevice;
            if (existing.TryGetValue(h, out var old)) { result.Add(old); continue; }

            var info = Marshal.AllocHGlobal(32);
            try
            {
                Marshal.WriteInt32(info, 0, 32);
                uint isz = 32;
                if ((int)Native.GetRawInputDeviceInfoW(h, Native.RIDI_DEVICEINFO, info, ref isz) <= 0) continue;
                ushort page = (ushort)Marshal.ReadInt16(info, 20), usage = (ushort)Marshal.ReadInt16(info, 22);
                if (page != 1 || (usage != 4 && usage != 5)) continue;

                var dev = new HidDevice
                {
                    Handle = h, UsagePage = page, Usage = usage,
                    Vid = (ushort)Marshal.ReadInt32(info, 8), Pid = (ushort)Marshal.ReadInt32(info, 12),
                };
                dev.Sony = SonyOf(dev.Vid, dev.Pid);
                dev.InterfacePath = ReadName(h);
                dev.InstanceId = dev.InterfacePath == null ? null : InstanceFromInterface(dev.InterfacePath);
                dev.ParseCaps();
                result.Add(dev);
            }
            finally { Marshal.FreeHGlobal(info); }
        }
        return result;
    }

    static string ReadName(IntPtr h)
    {
        uint size = 0;
        Native.GetRawInputDeviceInfoW(h, Native.RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0) return null;
        var buf = Marshal.AllocHGlobal((int)size * 2 + 2);
        try
        {
            return (int)Native.GetRawInputDeviceInfoW(h, Native.RIDI_DEVICENAME, buf, ref size) > 0 ? Marshal.PtrToStringUni(buf) : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    void ParseCaps()
    {
        uint size = 0;
        Native.GetRawInputDeviceInfoW(Handle, Native.RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return;
        Preparsed = Marshal.AllocHGlobal((int)size);
        if ((int)Native.GetRawInputDeviceInfoW(Handle, Native.RIDI_PREPARSEDDATA, Preparsed, ref size) <= 0) return;
        ParseCapsCore();
    }

    void ParseCapsCore()
    {
        var caps = new byte[64];
        if (Native.HidP_GetCaps(Preparsed, caps) != Native.HIDP_STATUS_SUCCESS) return;
        ushort nBtn = BitConverter.ToUInt16(caps, 46), nVal = BitConverter.ToUInt16(caps, 48);
        Usage = BitConverter.ToUInt16(caps, 0); UsagePage = BitConverter.ToUInt16(caps, 2);
        InputLen = BitConverter.ToUInt16(caps, 4);
        OutputLen = BitConverter.ToUInt16(caps, 6);

        if (nBtn > 0)
        {
            var b = new byte[72 * nBtn]; ushort len = nBtn;
            if (Native.HidP_GetButtonCaps(0, b, ref len, Preparsed) == Native.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < len; i++)
                {
                    int o = i * 72;
                    ushort page = BitConverter.ToUInt16(b, o);
                    bool range = b[o + 12] != 0;
                    ushort min = BitConverter.ToUInt16(b, o + 56), max = range ? BitConverter.ToUInt16(b, o + 58) : min;
                    if (page >= 0xFF00 || max < min || max - min > 128) continue;
                    if (!ButtonRanges.Contains((page, min, max))) ButtonRanges.Add((page, min, max));
                }
            }
        }
        if (nVal > 0)
        {
            var b = new byte[72 * nVal]; ushort len = nVal;
            if (Native.HidP_GetValueCaps(0, b, ref len, Preparsed) == Native.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < len; i++)
                {
                    int o = i * 72;
                    ushort page = BitConverter.ToUInt16(b, o);
                    bool range = b[o + 12] != 0;
                    ushort bits = BitConverter.ToUInt16(b, o + 18), count = BitConverter.ToUInt16(b, o + 20);
                    int lmin = BitConverter.ToInt32(b, o + 40), lmax = BitConverter.ToInt32(b, o + 44);
                    ushort umin = BitConverter.ToUInt16(b, o + 56), umax = range ? BitConverter.ToUInt16(b, o + 58) : umin;
                    if (page >= 0xFF00 || (!range && count > 1) || umax < umin || umax - umin > 32) continue;
                    for (int u = umin; u <= umax; u++)
                    {
                        if (Values.Any(v => v.Page == page && v.Usage == u)) continue;
                        Values.Add(new ValueInfo { Page = page, Usage = (ushort)u, Bits = bits, LMin = lmin, LMax = lmax });
                    }
                }
            }
        }
        ButtonRanges.Sort((a, c) => a.Page != c.Page ? a.Page.CompareTo(c.Page) : a.Min.CompareTo(c.Min));
        Values.Sort((a, c) => a.Page != c.Page ? a.Page.CompareTo(c.Page) : a.Usage.CompareTo(c.Usage));
        buttonPages = ButtonRanges.Select(r => r.Page).Distinct().ToArray();
        ParseOk = true;
    }

    public void Process(byte[] report, long now)
    {
        if (Preparsed == IntPtr.Zero) return;   // already disposed
        Stats.Hit(now);
        LastReport = report;
        // After an output report over Bluetooth, Sony pads switch to "extended" input reports (0x11 / 0x31)
        // that Windows' generic descriptor doesn't describe, so decode those by hand.
        if ((Sony == SonyKind.DualShock4 && report.Length >= 13 && report[0] == 0x11) ||
            (Sony == SonyKind.DualSense && report.Length >= 13 && report[0] == 0x31))
        {
            ParseSonyExtended(report);
            return;
        }
        if (!ParseOk) return;
        uint rlen = (uint)report.Length;

        foreach (var page in buttonPages)
        {
            uint n = (uint)usageBuf.Length;
            if (Native.HidP_GetUsages(0, page, 0, usageBuf, ref n, Preparsed, report, rlen) != Native.HIDP_STATUS_SUCCESS) continue;
            Pressed.RemoveWhere(k => (k >> 16) == page);
            for (int i = 0; i < n; i++) Pressed.Add(((uint)page << 16) | usageBuf[i]);
        }
        foreach (var v in Values)
        {
            if (Native.HidP_GetUsageValue(0, v.Page, 0, v.Usage, out uint raw, Preparsed, report, rlen) != Native.HIDP_STATUS_SUCCESS) continue;
            int val = (int)raw;
            if (v.LMin < 0 && v.Bits is > 0 and < 32 && (raw & (1u << (v.Bits - 1))) != 0)
                val = (int)(raw | ~((1u << v.Bits) - 1));
            v.Raw = val; v.Has = true;
        }
    }

    void ParseSonyExtended(byte[] r)
    {
        bool ds4 = Sony == SonyKind.DualShock4;
        int o = ds4 ? 3 : 2;                       // offset of the left-stick X byte
        byte lx = r[o], ly = r[o + 1], rx = r[o + 2], ry = r[o + 3], l2, r2, b0, b1, b2;
        if (ds4) { b0 = r[o + 4]; b1 = r[o + 5]; b2 = r[o + 6]; l2 = r[o + 7]; r2 = r[o + 8]; }
        else     { l2 = r[o + 4]; r2 = r[o + 5]; b0 = r[o + 7]; b1 = r[o + 8]; b2 = r[o + 9]; }

        void Axis(ushort usage, byte b)
        {
            var v = Values.FirstOrDefault(x => x.Page == 1 && x.Usage == usage);
            if (v == null) return;
            v.Raw = v.LMax > v.LMin ? v.LMin + (int)Math.Round(b * (double)(v.LMax - v.LMin) / 255) : b;
            v.Has = true;
        }
        // Same layout Windows' generic Sony descriptor uses: X/Y left, Z/Rz right, Rx/Ry triggers.
        Axis(0x30, lx); Axis(0x31, ly); Axis(0x32, rx); Axis(0x35, ry); Axis(0x33, l2); Axis(0x34, r2);
        var hat = Values.FirstOrDefault(x => x.IsHat);
        if (hat != null) { int d = b0 & 0x0F; hat.Raw = d <= 7 ? hat.LMin + d : hat.LMax + 1; hat.Has = true; }

        Pressed.RemoveWhere(k => (k >> 16) == 9);
        void Btn(int n, bool on) { if (on) Pressed.Add((9u << 16) | (uint)n); }
        for (int i = 0; i < 4; i++) Btn(1 + i, (b0 & (0x10 << i)) != 0);   // □ ✕ ○ △
        for (int i = 0; i < 8; i++) Btn(5 + i, (b1 & (1 << i)) != 0);      // L1 R1 L2 R2 Share Options L3 R3
        Btn(13, (b2 & 1) != 0); Btn(14, (b2 & 2) != 0);                   // PS, touchpad
    }

    /// Sets the two rumble motors (0-255). Returns null on success or an error message.
    public string Rumble(byte strong, byte weak)
    {
        if (!CanRumble) return "Rumble isn't supported for this controller.";
        byte[] r;
        bool bt = IsBluetooth;
        if (Sony == SonyKind.DualShock4 && !bt)
        {
            r = new byte[Math.Max(OutputLen, 32)];
            r[0] = 0x05; r[1] = 0x01;              // report 5, flags: motors only (leave the light bar alone)
            r[4] = weak; r[5] = strong;
        }
        else if (Sony == SonyKind.DualShock4)
        {
            r = new byte[78];
            r[0] = 0x11; r[1] = 0xC0; r[3] = 0x01; // report 0x11, HID+CRC, motors only
            r[6] = weak; r[7] = strong;
            AppendBtCrc(r);
        }
        else if (!bt)
        {
            r = new byte[Math.Max(OutputLen, 48)];
            r[0] = 0x02; r[1] = 0x03;              // report 2, compatible-vibration + haptics select
            r[3] = weak; r[4] = strong;
        }
        else
        {
            r = new byte[78];
            r[0] = 0x31; r[1] = (byte)((btSeq++ & 0x0F) << 4); r[2] = 0x10;
            r[3] = 0x03;
            r[5] = weak; r[6] = strong;
            AppendBtCrc(r);
        }
        try
        {
            if (writeHandle == null || writeHandle.IsInvalid)
            {
                writeHandle = Native.CreateFileW(InterfacePath, 0x40000000 /*GENERIC_WRITE*/, 3 /*share read|write*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
                if (writeHandle.IsInvalid) { int e = Marshal.GetLastWin32Error(); writeHandle = null; return $"Couldn't open the controller for output ({new System.ComponentModel.Win32Exception(e).Message})."; }
            }
            // Bluetooth reports carry a CRC, so they must go out at exactly 78 bytes; use the control pipe if a plain write is refused.
            if (Native.WriteFile(writeHandle, r, (uint)r.Length, out _, IntPtr.Zero)) return null;
            if (OutputLen > r.Length)
            {
                var padded = new byte[OutputLen];
                r.CopyTo(padded, 0);
                if (Native.WriteFile(writeHandle, padded, (uint)padded.Length, out _, IntPtr.Zero)) return null;
            }
            if (Native.HidD_SetOutputReport(writeHandle, r, (uint)r.Length)) return null;
            return $"The controller refused the rumble report ({new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}).";
        }
        catch (Exception ex) { return ex.Message; }
    }

    static void AppendBtCrc(byte[] r)
    {
        // Sony BT output CRC32 covers a 0xA2 header byte followed by the first 74 bytes.
        uint crc = Crc32(new byte[] { 0xA2 }, 0xFFFFFFFF);
        crc = Crc32(r.AsSpan(0, 74).ToArray(), crc) ^ 0xFFFFFFFF;
        BitConverter.GetBytes(crc).CopyTo(r, 74);
    }

    static uint[] crcTable;
    static uint Crc32(byte[] data, uint crc)
    {
        if (crcTable == null)
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[i] = c; }
            crcTable = t;
        }
        foreach (var b in data) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public void Dispose()
    {
        ParseOk = false;
        if (Direct)
        {
            reading = false;
            if (readHandle != null && !readHandle.IsInvalid) Native.CancelIoEx(readHandle, IntPtr.Zero);
            readThread?.Join(300);
            readHandle?.Dispose(); readHandle = null;
            if (Preparsed != IntPtr.Zero) { Native.HidD_FreePreparsedData(Preparsed); Preparsed = IntPtr.Zero; }
        }
        else if (Preparsed != IntPtr.Zero) { Marshal.FreeHGlobal(Preparsed); Preparsed = IntPtr.Zero; }
        writeHandle?.Dispose(); writeHandle = null;
    }
}

// ───────────────────────────── XInput ─────────────────────────────
sealed class XSlot
{
    public int Index;
    public bool Connected;
    public Native.XINPUT_STATE State;
    public uint LastPacket;
    public readonly RateStats Stats = new();
    public byte BatteryType, BatteryLevel;
    public long NextProbe, NextBattery;
}

sealed class XInputMonitor : IDisposable
{
    public readonly XSlot[] Slots = Enumerable.Range(0, 4).Select(i => new XSlot { Index = i }).ToArray();
    readonly Native.XInputGetStateFn getState;
    Thread thread;
    volatile bool running;
    public bool Available { get; }

    public XInputMonitor()
    {
        try
        {
            var mod = Native.LoadLibraryW("xinput1_4.dll");
            if (mod == IntPtr.Zero) return;
            Available = true;
            // Ordinal 100 = XInputGetStateEx (also reports the Guide button)
            var ex = Native.GetProcAddress(mod, (IntPtr)100);
            getState = ex != IntPtr.Zero
                ? Marshal.GetDelegateForFunctionPointer<Native.XInputGetStateFn>(ex)
                : Native.XInputGetState;
        }
        catch { Available = false; }
    }

    public void Start()
    {
        if (!Available) return;
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "XInput poll", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    void Loop()
    {
        long f = Stopwatch.Frequency;
        while (running)
        {
            long now = Stopwatch.GetTimestamp();
            foreach (var s in Slots)
            {
                // Polling an empty slot is slow, so only probe empty slots once a second.
                if (!s.Connected && now < s.NextProbe) continue;
                uint r = getState((uint)s.Index, out var st);
                bool readBattery = false;
                lock (s)
                {
                    if (r == 0)
                    {
                        if (!s.Connected) { s.Connected = true; s.LastPacket = st.dwPacketNumber; s.NextBattery = 0; }
                        else if (st.dwPacketNumber != s.LastPacket) { s.LastPacket = st.dwPacketNumber; s.Stats.Hit(now); }
                        s.State = st;
                        if (now >= s.NextBattery) { readBattery = true; s.NextBattery = now + 2 * f; }
                    }
                    else { s.Connected = false; s.NextProbe = now + f; }
                }
                if (readBattery && Native.XInputGetBatteryInformation((uint)s.Index, 0, out var bi) == 0)
                    lock (s) { s.BatteryType = bi.BatteryType; s.BatteryLevel = bi.BatteryLevel; }
            }
            Thread.Sleep(1);
        }
    }

    public void Rumble(int idx, bool on)
    {
        if (!Available) return;
        var v = new Native.XINPUT_VIBRATION { wLeftMotorSpeed = (ushort)(on ? 42000 : 0), wRightMotorSpeed = (ushort)(on ? 42000 : 0) };
        try { Native.XInputSetState((uint)idx, ref v); } catch { }
    }

    public void Dispose()
    {
        running = false;
        thread?.Join(200);
        for (int i = 0; i < 4; i++) Rumble(i, false);
    }
}

// ───────────────────────────── Enable / disable (SetupAPI, like Device Manager) ─────────────────────────────
static class DeviceState
{
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_PROPCHANGE_PARAMS { public uint HeaderSize, InstallFunction, StateChange, Scope, HwProfile; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SP_DEVINSTALL_PARAMS
    {
        public uint cbSize, Flags, FlagsEx;
        public IntPtr hwndParent, InstallMsgHandler, InstallMsgHandlerContext, FileQueue;
        public UIntPtr ClassInstallReserved;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
    }

    const uint DIF_PROPERTYCHANGE = 0x12, DICS_ENABLE = 1, DICS_DISABLE = 2, DICS_FLAG_GLOBAL = 1;
    const uint DI_NEEDRESTART = 0x80, DI_NEEDREBOOT = 0x100;
    static readonly IntPtr INVALID = new(-1);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwnd);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwnd, uint flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiSetClassInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_PROPCHANGE_PARAMS p, uint size);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(uint fn, IntPtr set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DEVINSTALL_PARAMS p);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    /// Returns 0 on success, 3010 if the change finishes after the device reconnects / PC restarts, else a Win32 error code.
    public static int Set(string instanceId, bool enable)
    {
        var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == INVALID) return Marshal.GetLastWin32Error();
        try
        {
            var dev = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref dev)) return Marshal.GetLastWin32Error();

            var pc = new SP_PROPCHANGE_PARAMS
            {
                HeaderSize = 8, InstallFunction = DIF_PROPERTYCHANGE,
                StateChange = enable ? DICS_ENABLE : DICS_DISABLE, Scope = DICS_FLAG_GLOBAL, HwProfile = 0,
            };
            if (!SetupDiSetClassInstallParamsW(set, ref dev, ref pc, (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>())) return Marshal.GetLastWin32Error();
            if (!SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref dev)) return Marshal.GetLastWin32Error();

            var ip = new SP_DEVINSTALL_PARAMS { cbSize = (uint)Marshal.SizeOf<SP_DEVINSTALL_PARAMS>() };
            if (SetupDiGetDeviceInstallParamsW(set, ref dev, ref ip) && (ip.Flags & (DI_NEEDRESTART | DI_NEEDREBOOT)) != 0) return 3010;
            return 0;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
