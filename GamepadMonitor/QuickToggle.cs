using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace GamepadMonitor;

// ───────────────────────────── Software "unplug and replug" ─────────────────────────────
/// Power-cycles the USB port a controller is plugged into (IOCTL_USB_HUB_CYCLE_PORT).
/// To Windows this is a real unplug, which programs can't veto — so a stuck disable/enable completes.
static class UsbPort
{
    const uint IOCTL_USB_HUB_CYCLE_PORT = 0x220444;

    static string Prop(string id, Native.DEVPROPKEY key)
    {
        if (Native.CM_Locate_DevNodeW(out uint inst, id, 0) != 0) return null;
        uint size = 0;
        Native.CM_Get_DevNode_PropertyW(inst, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        if (Native.CM_Get_DevNode_PropertyW(inst, ref key, out uint type, buf, ref size, 0) != 0) return null;
        return type == 7 /*UINT32*/ ? BitConverter.ToUInt32(buf, 0).ToString() : Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    static readonly Native.DEVPROPKEY PKEY_Address = new("a45c254e-df1c-4efd-8020-67d146a850e0", 30);

    /// Finds the hub + port for a USB controller. Returns null (with a reason) for Bluetooth/virtual devices.
    public static (string HubPath, uint Port, string UsbDeviceId)? Locate(string topId, out string reason)
    {
        reason = null;
        string cur = topId;
        for (int guard = 0; cur != null && guard < 16; guard++)
        {
            var up = cur.ToUpperInvariant();
            if (up.StartsWith(@"BTH") || up.Contains("{00001124-0000-1000-8000-00805F9B34FB}"))
            { reason = "it's connected over Bluetooth"; return null; }
            if (up.StartsWith(@"ROOT\") || up.StartsWith(@"SWD\"))
            { reason = "it's a virtual controller"; return null; }
            string parent = Prop(cur, Native.PKEY_Parent);
            if (up.StartsWith(@"USB\") && parent != null)
            {
                var hubs = Native.InterfacesOf(Native.GUID_DEVINTERFACE_USB_HUB, parent);
                if (hubs.Count > 0)
                {
                    if (!uint.TryParse(Prop(cur, PKEY_Address), out uint port) || port == 0)
                    { reason = "Windows didn't report its USB port number"; return null; }
                    return (hubs[0], port, cur);
                }
            }
            cur = parent;
        }
        reason = "it isn't a USB device";
        return null;
    }

    /// Returns 0 on success, otherwise a Win32 error code.
    public static int Cycle(string hubPath, uint port)
    {
        using var hub = Native.CreateFileW(hubPath, 0x40000000 /*GENERIC_WRITE*/, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (hub.IsInvalid) return Marshal.GetLastWin32Error();
        var p = new byte[8];
        BitConverter.GetBytes(port).CopyTo(p, 0);               // USB_CYCLE_PORT_PARAMS { ConnectionIndex, StatusReturned }
        return Native.DeviceIoControl(hub, IOCTL_USB_HUB_CYCLE_PORT, p, 8, p, 8, out _, IntPtr.Zero) ? 0 : Marshal.GetLastWin32Error();
    }

    /// Waits (up to timeoutMs) for the device to come back in the wanted state.
    public static bool WaitFor(string id, bool wantDisabled, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        Thread.Sleep(600);
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Native.CM_Locate_DevNodeW(out uint inst, id, 0) == 0 && Native.CM_Get_DevNode_Status(out uint st, out uint prob, inst, 0) == 0)
            {
                bool disabled = (st & Native.DN_HAS_PROBLEM) != 0 && prob == Native.CM_PROB_DISABLED;
                bool started = (st & 0x8 /*DN_STARTED*/) != 0;
                if (wantDisabled ? disabled : started) return true;
            }
            Thread.Sleep(250);
        }
        return false;
    }
}

// ───────────────────────────── HidHide (hide controllers from other apps) ─────────────────────────────
static class HidHide
{
    const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\HidHide";

    public static string CliPath
    {
        get
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var p in new[]
            {
                Path.Combine(pf, "Nefarius Software Solutions", "HidHide", "x64", "HidHideCLI.exe"),
                Path.Combine(pf, "Nefarius Software Solutions e.U", "HidHide", "x64", "HidHideCLI.exe"),
                Path.Combine(pf, "Nefarius Software Solutions", "HidHide", "HidHideCLI.exe"),
            })
                if (File.Exists(p)) return p;
            return null;
        }
    }

    public static bool Installed
    {
        get
        {
            try { using var k = Registry.LocalMachine.OpenSubKey(ServiceKey); if (k != null && CliPath != null) return true; } catch { }
            return false;
        }
    }

    static string LocalStateFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GamepadMonitor", "hidden.txt");

    /// Device instance IDs currently on HidHide's hide list. Read from the driver's registry settings when
    /// possible; otherwise falls back to what this app last set.
    public static HashSet<string> HiddenIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Installed) return set;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ServiceKey + @"\Parameters");
            if (k != null)
            {
                foreach (var name in k.GetValueNames())
                    if (k.GetValueKind(name) == RegistryValueKind.MultiString && name.Contains("Device", StringComparison.OrdinalIgnoreCase))
                        foreach (var s in (string[])k.GetValue(name)) set.Add(s.Trim());
                return set;
            }
        }
        catch { }
        try { if (File.Exists(LocalStateFile)) foreach (var l in File.ReadAllLines(LocalStateFile)) if (l.Trim().Length > 0) set.Add(l.Trim()); } catch { }
        return set;
    }

    public static void RememberLocally(IEnumerable<string> ids, bool hidden)
    {
        try
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(LocalStateFile)) foreach (var l in File.ReadAllLines(LocalStateFile)) if (l.Trim().Length > 0) set.Add(l.Trim());
            foreach (var id in ids) { if (hidden) set.Add(id); else set.Remove(id); }
            Directory.CreateDirectory(Path.GetDirectoryName(LocalStateFile)!);
            File.WriteAllLines(LocalStateFile, set);
        }
        catch { }
    }

    /// Hides or shows devices via HidHideCLI (needs admin). Always allow-lists this app so it can keep reading them.
    /// Returns 0 on success, 2 if HidHide isn't installed, or the first failing CLI exit code.
    public static int Apply(bool hide, IEnumerable<string> ids, StringBuilder log)
    {
        var cli = CliPath;
        if (cli == null) { log.AppendLine("HidHide isn't installed."); return 2; }
        int Run(string args)
        {
            var p = Process.Start(new ProcessStartInfo(cli, args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            log.AppendLine($"> HidHideCLI {args}  (exit {p.ExitCode})");
            if (o.Trim().Length > 0) log.AppendLine(o.Trim());
            return p.ExitCode;
        }
        int first = 0;
        void Keep(int c) { if (first == 0 && c != 0) first = c; }
        if (hide)
        {
            Keep(Run($"--app-reg \"{Environment.ProcessPath}\""));
            Keep(Run("--cloak-on"));
            foreach (var id in ids) Keep(Run($"--dev-hide \"{id}\""));
        }
        else foreach (var id in ids) Keep(Run($"--dev-unhide \"{id}\""));
        return first;
    }
}

// ───────────────────────────── Privileged actions (run in-process when admin, else via an elevated copy) ─────────────────────────────
static class Privileged
{
    public const int PendingReconnect = 3010;

    /// Entry point for the elevated helper. Returns the process exit code.
    ///   --set-state enable|disable "<id>"
    ///   --hidhide hide|show "<logfile>" "<topId>" "<id>"...
    public static int Dispatch(string[] args)
    {
        if (args.Length == 3 && args[0] == "--set-state")
            return SetStateAndReplug(args[2], args[1] == "enable", null);
        if (args.Length == 3 && args[0] == "--replug")
            return Replug(args[2], args[1] == "enable", null);
        if (args.Length >= 5 && args[0] == "--hidhide")
        {
            var log = new StringBuilder();
            int code = HideAndReplug(args[1] == "hide", args[3], args.Skip(4).ToArray(), log);
            try { File.WriteAllText(args[2], log.ToString()); } catch { }
            return code;
        }
        return 87; // ERROR_INVALID_PARAMETER
    }

    public static bool IsHelperCall(string[] args) => args.Length > 0 && (args[0] == "--set-state" || args[0] == "--hidhide" || args[0] == "--replug");

    static readonly Native.DEVPROPKEY PKEY_ConfigFlags = new("a45c254e-df1c-4efd-8020-67d146a850e0", 12);

    /// The device's real state: is it marked disabled, is it actually stopped, is it running?
    public static (bool Marked, bool Disabled, bool Started) Probe(string id)
    {
        if (Native.CM_Locate_DevNodeW(out uint inst, id, 0) != 0) return (false, false, false);
        Native.CM_Get_DevNode_Status(out uint st, out uint prob, inst, 0);
        uint flags = 0, size = 4; var key = PKEY_ConfigFlags; var buf = new byte[4];
        if (Native.CM_Get_DevNode_PropertyW(inst, ref key, out _, buf, ref size, 0) == 0) flags = BitConverter.ToUInt32(buf, 0);
        bool disabled = (st & Native.DN_HAS_PROBLEM) != 0 && prob == Native.CM_PROB_DISABLED;
        return ((flags & 1) != 0, disabled, (st & 0x8) != 0 && !disabled);
    }

    /// Enable/disable like Device Manager; if programs are holding it, replug the USB port in software.
    public static int SetStateAndReplug(string id, bool enable, StringBuilder log)
    {
        int code = DeviceState.Set(id, enable);
        // Don't trust the return code alone: when programs veto a disable, SetupAPI sometimes reports an odd error
        // (e.g. 13 "data is invalid") even though Windows has marked the change. Look at what actually happened.
        var s = Probe(id);
        bool done = enable ? (!s.Marked && s.Started) : s.Disabled;
        bool pending = enable ? (!s.Marked && s.Disabled) : (s.Marked && !s.Disabled);
        if (done) return 0;
        if (!pending) return code == 0 ? PendingReconnect : code;
        return Replug(id, enable, log);
    }

    /// Finishes a pending enable/disable by replugging the controller's USB port in software.
    public static int Replug(string id, bool enable, StringBuilder log)
    {
        var loc = UsbPort.Locate(id, out var why);
        if (loc == null) { log?.AppendLine($"Couldn't replug it in software because {why}."); return PendingReconnect; }
        int c = UsbPort.Cycle(loc.Value.HubPath, loc.Value.Port);
        if (c != 0) { log?.AppendLine($"Software replug failed: {new System.ComponentModel.Win32Exception(c).Message}"); return PendingReconnect; }
        return UsbPort.WaitFor(id, wantDisabled: !enable, timeoutMs: 8000) ? 0 : PendingReconnect;
    }

    /// Hide/show with HidHide, then replug (USB) so programs that already had it open lose it too.
    public static int HideAndReplug(bool hide, string topId, string[] ids, StringBuilder log)
    {
        int code = HidHide.Apply(hide, ids, log);
        if (code != 0) return code;
        var loc = UsbPort.Locate(topId, out var why);
        if (loc == null)
        {
            log.AppendLine($"Not replugged in software ({why}): programs that already had it open keep it until they restart or the controller reconnects.");
            return PendingReconnect;
        }
        int c = UsbPort.Cycle(loc.Value.HubPath, loc.Value.Port);
        if (c != 0) { log.AppendLine($"Software replug failed: {new System.ComponentModel.Win32Exception(c).Message}"); return PendingReconnect; }
        UsbPort.WaitFor(topId, wantDisabled: false, timeoutMs: 8000);
        return 0;
    }
}
