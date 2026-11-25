using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public sealed class AdbWirelessPairingWindow : EditorWindow
{
    private const string PrefsPrefix = "ADBWirelessPairing.";
    private const string AdbPathKey = PrefsPrefix + "AdbPath";
    private const string HostKey = PrefsPrefix + "Host";
    private const string PairPortKey = PrefsPrefix + "PairPort";
    private const string ConnectPortKey = PrefsPrefix + "ConnectPort";
    private const string DeviceListKey = PrefsPrefix + "Devices";
    private static GUIStyle _sectionTitleStyle;

    private string _adbPath;
    private string _host;
    private string _pairPort;
    private string _connectPort;
    private string _pairingCode;
    private string _lastCommandOutput = string.Empty;
    private bool _isRunning;
    private bool _showAdvancedAdbPath;
    private bool _showPairing;
    private readonly List<WirelessAdbDevice> _managedDevices = new List<WirelessAdbDevice>();

    [MenuItem("Tools/Android/ADB Wireless Pairing")]
    private static void Open()
    {
        GetWindow<AdbWirelessPairingWindow>("ADB Wireless");
    }

    private void OnEnable()
    {
        _adbPath = EditorPrefs.GetString(AdbPathKey, FindAdbPath());
        _host = EditorPrefs.GetString(HostKey, string.Empty);
        _pairPort = EditorPrefs.GetString(PairPortKey, string.Empty);
        _connectPort = EditorPrefs.GetString(ConnectPortKey, string.Empty);
        LoadManagedDevices();
        RefreshConnectedDevices();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Wireless ADB", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Enable Developer options > Wireless debugging on the phone and connect both devices to the same Wi-Fi. Pairing and connection use different ports; copy each value from the phone because they can change.",
            MessageType.Info);

        DrawSectionTitle("Add new Device");
        DrawAdbDiscovery();
        DrawStepDivider();
        EditorGUILayout.LabelField("Device IP", EditorStyles.boldLabel);
        DrawDeviceAddress();
        DrawStepDivider();
        DrawConnectionSection();
        DrawManagedDevices();
    }

    private void DrawAdbDiscovery()
    {
        EditorGUILayout.LabelField("ADB Path", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(_isRunning))
        {
            if (GUILayout.Button("Auto-find ADB", GUILayout.Width(120f)))
                FindAndSelectAdb();

            if (GUILayout.Button("Browse...", GUILayout.Width(84f)))
            {
                var selectedPath = EditorUtility.OpenFilePanel(
                    "Select adb executable",
                    GetInitialDirectory(),
                    GetAdbFileName());
                if (!string.IsNullOrEmpty(selectedPath))
                    SetAdbPath(selectedPath);
            }
        }

        var adbFound = IsAdbAvailable();
        var oldContentColor = GUI.contentColor;
        GUI.contentColor = adbFound
            ? new Color(0.25f, 0.8f, 0.35f)
            : new Color(0.95f, 0.3f, 0.3f);
        GUILayout.Label(adbFound ? "VALID" : "INVALID",
            EditorStyles.boldLabel, GUILayout.Width(70f));
        GUI.contentColor = oldContentColor;
        EditorGUILayout.EndHorizontal();

        _showAdvancedAdbPath = EditorGUILayout.Foldout(_showAdvancedAdbPath, "Show ADB Path");
        if (_showAdvancedAdbPath)
            _adbPath = EditorGUILayout.TextField("Executable", _adbPath);

        EditorPrefs.SetString(AdbPathKey, _adbPath ?? string.Empty);
    }

    private void DrawDeviceAddress()
    {
        _host = DrawPlaceholderTextField(
            "ADBDeviceHost",
            "Phone IP / host",
            _host,
            "192.168.1.25");
        EditorGUILayout.HelpBox(
            "Enter the phone's IP address shown in Wireless debugging.",
            MessageType.None);

        EditorPrefs.SetString(HostKey, _host ?? string.Empty);
    }

    private void DrawPairingSection()
    {
        _pairPort = DrawPlaceholderTextField(
            "ADBPairingPort",
            "Pairing port",
            _pairPort,
            "Example: 37123");
        _pairingCode = DrawPlaceholderPasswordField(
            "ADBPairingCode",
            "Six-digit code",
            _pairingCode,
            "Example: 123456");

        var canPair = !_isRunning &&
                      IsAdbAvailable() &&
                      IsEndpointValid(_pairPort) &&
                      !string.IsNullOrWhiteSpace(_pairingCode);

        using (new EditorGUI.DisabledScope(!canPair))
        {
            if (GUILayout.Button("Pair this computer", GUILayout.Height(28f)))
                RunPair();
        }
        EditorPrefs.SetString(PairPortKey, _pairPort ?? string.Empty);
    }

    private void DrawConnectionSection()
    {
        EditorGUILayout.LabelField("Connection Port", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Enter the connection port shown on the phone's main Wireless debugging screen.",
            MessageType.None);

        _connectPort = DrawPlaceholderTextField(
            "ADBConnectionPort",
            "Connection port",
            _connectPort,
            "Example: 39517");

        using (new EditorGUI.DisabledScope(_isRunning || !IsAdbAvailable() ||
                                           !IsEndpointValid(_connectPort)))
        {
            if (GUILayout.Button("Connect"))
                TryConnect();
        }

        EditorPrefs.SetString(ConnectPortKey, _connectPort ?? string.Empty);

        if (_showPairing)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Pair this computer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "If connection failed because this computer is no longer authorized, pair it again: tap 'Pair device with pairing code' on the phone and enter the temporary pairing port and code below. Otherwise, check the phone IP and connection port.",
                MessageType.Warning);
            DrawPairingSection();
        }
    }

    private static void DrawStepDivider()
    {
        GUILayout.Space(10f);
        var dividerRect = EditorGUILayout.GetControlRect(false, 1f);
        EditorGUI.DrawRect(dividerRect, EditorGUIUtility.isProSkin
            ? new Color(0.35f, 0.35f, 0.35f)
            : new Color(0.65f, 0.65f, 0.65f));
        GUILayout.Space(10f);
    }

    private static void DrawSectionTitle(string title)
    {
        if (_sectionTitleStyle == null)
        {
            _sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = EditorStyles.boldLabel.fontSize + 2,
                normal =
                {
                    textColor = EditorGUIUtility.isProSkin
                        ? new Color(0.82f, 0.9f, 1f)
                        : new Color(0.08f, 0.24f, 0.42f)
                }
            };
        }

        GUILayout.Space(8f);
        var titleRect = EditorGUILayout.GetControlRect(false, 32f);
        EditorGUI.DrawRect(
            titleRect,
            EditorGUIUtility.isProSkin
                ? new Color(0.12f, 0.25f, 0.38f)
                : new Color(0.78f, 0.87f, 0.96f));
        EditorGUI.LabelField(titleRect, title.ToUpperInvariant(), _sectionTitleStyle);
        GUILayout.Space(6f);
    }

    private void DrawManagedDevices()
    {
        GUILayout.Space(14f);
        DrawStepDivider();
        GUILayout.Space(12f);
        DrawSectionTitle("Device list");
        EditorGUILayout.HelpBox(
            "Disconnect keeps a device in this list for reconnecting. Remove disconnects it first if necessary, then removes it from this list; it does not forget pairing on the phone.",
            MessageType.None);
        var connectedCount = _managedDevices.FindAll(device => device.IsConnected).Count;
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(_isRunning || !IsAdbAvailable()))
        {
            if (GUILayout.Button("Refresh", GUILayout.Width(80f)))
                RefreshConnectedDevices();
        }
        EditorGUILayout.LabelField(
            connectedCount + " connected / " + _managedDevices.Count + " in list",
            EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();

        if (_managedDevices.Count == 0)
        {
            EditorGUILayout.HelpBox("No wireless devices in the list. Connect a phone above to add it.", MessageType.None);
            return;
        }

        WirelessAdbDevice actionDevice = null;
        var action = DeviceAction.None;
        foreach (var device in _managedDevices)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(device.DisplayName, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(device.Endpoint, EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
            var oldContentColor = GUI.contentColor;
            GUI.contentColor = device.IsConnected
                ? new Color(0.25f, 0.8f, 0.35f)
                : new Color(0.9f, 0.3f, 0.3f);
            EditorGUILayout.LabelField(
                device.IsConnected ? "Connected" : "Disconnected",
                EditorStyles.boldLabel,
                GUILayout.Width(92f));
            GUI.contentColor = oldContentColor;
            using (new EditorGUI.DisabledScope(_isRunning))
            {
                using (new EditorGUI.DisabledScope(!IsAdbAvailable()))
                {
                    if (GUILayout.Button(device.IsConnected ? "Disconnect" : "Reconnect", GUILayout.Width(86f)))
                    {
                        actionDevice = device;
                        action = device.IsConnected ? DeviceAction.Disconnect : DeviceAction.Reconnect;
                    }
                }

                if (GUILayout.Button("Remove", GUILayout.Width(64f)))
                {
                    actionDevice = device;
                    action = DeviceAction.Remove;
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        if (actionDevice != null)
        {
            switch (action)
            {
                case DeviceAction.Disconnect:
                    RunDisconnect(actionDevice);
                    break;
                case DeviceAction.Reconnect:
                    RunReconnect(actionDevice);
                    break;
                case DeviceAction.Remove:
                    RemoveManagedDevice(actionDevice);
                    break;
            }
        }
    }

    private void FindAndSelectAdb()
    {
        var candidates = new System.Collections.Generic.HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var sdkRoots = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            EditorPrefs.GetString("AndroidSdkRoot", string.Empty),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Android", "Sdk"),
            Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines", "AndroidPlayer", "SDK"),
            Path.GetFullPath(Path.Combine(EditorApplication.applicationContentsPath, "..", "AndroidPlayer", "SDK"))
        };

        foreach (var sdkRoot in sdkRoots)
        {
            if (!string.IsNullOrWhiteSpace(sdkRoot))
                candidates.Add(Path.Combine(sdkRoot, "platform-tools", GetAdbFileName()));
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(directory))
                    candidates.Add(Path.Combine(directory.Trim().Trim('"'), GetAdbFileName()));
            }
        }

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;

            SetAdbPath(candidate);
            AdbStatusPopup.Show(
                "ADB found",
                "Using ADB at:" + Environment.NewLine + candidate,
                AdbStatusPopup.Status.Success);
            return;
        }

        AdbStatusPopup.Show(
            "ADB not found",
            "ADB was not found in Android SDK locations or PATH. Use Browse to select adb in platform-tools.",
            AdbStatusPopup.Status.Error);
    }

    private void SetAdbPath(string path)
    {
        _adbPath = path;
        EditorPrefs.SetString(AdbPathKey, _adbPath);
    }

    private static string DrawPlaceholderTextField(
        string controlName,
        string label,
        string value,
        string placeholder)
    {
        var row = EditorGUILayout.GetControlRect();
        var field = EditorGUI.PrefixLabel(row, new GUIContent(label));
        GUI.SetNextControlName(controlName);
        var result = EditorGUI.TextField(field, value);

        if (string.IsNullOrEmpty(result) && GUI.GetNameOfFocusedControl() != controlName)
        {
            var oldColor = GUI.color;
            GUI.color = new Color(oldColor.r, oldColor.g, oldColor.b, 0.55f);
            GUI.Label(field, placeholder, EditorStyles.textField);
            GUI.color = oldColor;
        }

        return result;
    }

    private static string DrawPlaceholderPasswordField(
        string controlName,
        string label,
        string value,
        string placeholder)
    {
        var row = EditorGUILayout.GetControlRect();
        var field = EditorGUI.PrefixLabel(row, new GUIContent(label));
        GUI.SetNextControlName(controlName);
        var result = EditorGUI.PasswordField(field, value);

        if (string.IsNullOrEmpty(result) && GUI.GetNameOfFocusedControl() != controlName)
        {
            var oldColor = GUI.color;
            GUI.color = new Color(oldColor.r, oldColor.g, oldColor.b, 0.55f);
            GUI.Label(field, placeholder, EditorStyles.textField);
            GUI.color = oldColor;
        }

        return result;
    }

    private void RunPair()
    {
        if (!IsEndpointValid(_pairPort))
        {
            AdbStatusPopup.Show(
                "Pairing details required",
                "Enter the phone IP and pairing port shown in the phone's 'Pair device with pairing code' dialog.",
                AdbStatusPopup.Status.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_pairingCode))
        {
            AdbStatusPopup.Show(
                "Pairing code required",
                "Enter the pairing code currently shown on the phone.",
                AdbStatusPopup.Status.Warning);
            return;
        }

        var paired = RunAdbCommand(
            "pair " + Quote(BuildEndpoint(_pairPort)) + " " + Quote(_pairingCode));
        _pairingCode = string.Empty;
        if (!paired || !Regex.IsMatch(_lastCommandOutput, @"(?i)successfully paired"))
        {
            AdbStatusPopup.Show(
                "Pairing failed",
                _lastCommandOutput,
                AdbStatusPopup.Status.Error);
            Repaint();
            return;
        }

        _showPairing = false;
        var pairOutput = _lastCommandOutput;
        if (IsEndpointValid(_connectPort))
        {
            var connectCommandSucceeded =
                RunAdbCommand("connect " + Quote(BuildEndpoint(_connectPort)));
            var connectOutput = _lastCommandOutput;
            var connected = connectCommandSucceeded &&
                            Regex.IsMatch(
                                connectOutput,
                                @"(?im)^\s*(connected to|already connected to)\s+");
            AdbStatusPopup.Show(
                connected ? "Paired and connected" : "Paired; connection failed",
                pairOutput + Environment.NewLine + Environment.NewLine +
                (connected
                    ? "Pairing succeeded and ADB connected to the phone."
                    : "Pairing succeeded, but connection failed. Check the phone IP and connection port.") +
                Environment.NewLine + Environment.NewLine + connectOutput,
                connected ? AdbStatusPopup.Status.Success : AdbStatusPopup.Status.Warning);
        }
        else
        {
            AdbStatusPopup.Show(
                "Pairing successful",
                pairOutput + Environment.NewLine +
                "Enter the connection port shown on the phone, then press Connect.",
                AdbStatusPopup.Status.Success);
        }

        RefreshConnectedDevices();
        Repaint();
    }

    private void TryConnect()
    {
        if (!IsEndpointValid(_connectPort))
        {
            AdbStatusPopup.Show(
                "Connection details required",
                "Enter the phone IP and connection port shown on the main Wireless debugging screen.",
                AdbStatusPopup.Status.Warning);
            return;
        }

        var commandSucceeded = RunAdbCommand("connect " + Quote(BuildEndpoint(_connectPort)));
        var connected = commandSucceeded &&
                        Regex.IsMatch(_lastCommandOutput, @"(?im)^\s*(connected to|already connected to)\s+");
        if (connected)
        {
            _showPairing = false;
            AdbStatusPopup.Show(
                "ADB connected",
                _lastCommandOutput + Environment.NewLine +
                "No pairing code is needed for this connection.",
                AdbStatusPopup.Status.Success);
        }
        else
        {
            var pairNow = AdbStatusPopup.Confirm(
                "ADB could not connect",
                _lastCommandOutput + Environment.NewLine + Environment.NewLine +
                "If this computer has not been paired with the phone before, pair it first using the temporary pairing port and six-digit code. If it was already paired, check that the phone IP and connection port are correct.\n\nPair this computer now?",
                "Pair first",
                "Not now",
                AdbStatusPopup.Status.Warning);
            _showPairing = pairNow;
        }

        RefreshConnectedDevices();
        Repaint();
    }

    private void RunDisconnect(WirelessAdbDevice device)
    {
        var commandSucceeded = RunAdbCommand("disconnect " + Quote(device.Endpoint));
        var commandOutput = _lastCommandOutput;
        RefreshConnectedDevices();
        var stillConnected = _managedDevices.Find(item =>
            item.IsConnected &&
            string.Equals(item.Endpoint, device.Endpoint, StringComparison.OrdinalIgnoreCase)) != null;
        var disconnected = commandSucceeded && !stillConnected;
        _lastCommandOutput = commandOutput;
        if (stillConnected)
            _lastCommandOutput += Environment.NewLine + "The device is still listed as connected.";

        Repaint();
        ShowCommandResult("ADB disconnect", disconnected);
    }

    private void RunReconnect(WirelessAdbDevice device)
    {
        var commandSucceeded = RunAdbCommand("connect " + Quote(device.Endpoint));
        var connected = commandSucceeded &&
                        Regex.IsMatch(_lastCommandOutput, @"(?im)^\s*(connected to|already connected to)\s+");
        var commandOutput = _lastCommandOutput;
        RefreshConnectedDevices();
        _lastCommandOutput = commandOutput;

        Repaint();
        ShowCommandResult("ADB reconnect", connected);
    }

    private void RemoveManagedDevice(WirelessAdbDevice device)
    {
        if (device.IsConnected)
        {
            var commandSucceeded = RunAdbCommand("disconnect " + Quote(device.Endpoint));
            var commandOutput = _lastCommandOutput;
            RefreshConnectedDevices();
            var stillConnected = _managedDevices.Find(item =>
                item.IsConnected &&
                string.Equals(item.Endpoint, device.Endpoint, StringComparison.OrdinalIgnoreCase)) != null;
            if (!commandSucceeded || stillConnected)
            {
                _lastCommandOutput = commandOutput;
                if (stillConnected)
                    _lastCommandOutput += Environment.NewLine + "The device is still connected, so it was not removed.";
                ShowCommandResult("Could not remove device", false);
                return;
            }
        }

        _managedDevices.Remove(device);
        SaveManagedDevices();
        Repaint();
    }

    private void ShowCommandResult(string title, bool succeeded)
    {
        AdbStatusPopup.Show(
            succeeded ? title + " successful" : title + " failed",
            _lastCommandOutput,
            succeeded ? AdbStatusPopup.Status.Success : AdbStatusPopup.Status.Error);
    }

    private bool RunAdbCommand(string arguments)
    {
        if (!File.Exists(_adbPath))
        {
            _lastCommandOutput = "adb was not found. Browse to adb.exe in Android SDK/platform-tools.";
            return false;
        }

        _isRunning = true;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var standardOutput = new StringBuilder();
            var standardError = new StringBuilder();
            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data != null)
                        standardOutput.AppendLine(eventArgs.Data);
                };
                process.ErrorDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data != null)
                        standardError.AppendLine(eventArgs.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(15000))
                {
                    process.Kill();
                    _lastCommandOutput = "ADB command timed out after 15 seconds.";
                    return false;
                }

                process.WaitForExit();
                _lastCommandOutput = standardOutput.ToString().Trim();
                if (standardError.Length > 0)
                    _lastCommandOutput += Environment.NewLine + "Errors:" +
                                          Environment.NewLine + standardError.ToString().Trim();
                if (string.IsNullOrWhiteSpace(_lastCommandOutput))
                    _lastCommandOutput = "ADB command completed with no output.";
                if (process.ExitCode != 0)
                    _lastCommandOutput += Environment.NewLine + "Exit code: " + process.ExitCode;
                return process.ExitCode == 0;
            }
        }
        catch (Exception exception)
        {
            _lastCommandOutput = "Failed to run ADB: " + exception.Message;
            return false;
        }
        finally
        {
            _isRunning = false;
            Repaint();
        }
    }

    private void RefreshConnectedDevices()
    {
        if (!IsAdbAvailable())
        {
            foreach (var device in _managedDevices)
                device.IsConnected = false;
            SaveManagedDevices();
            Repaint();
            return;
        }

        if (_isRunning)
            return;

        if (!RunAdbCommand("devices"))
        {
            AdbStatusPopup.Show(
                "Could not read ADB devices",
                _lastCommandOutput,
                AdbStatusPopup.Status.Error);
            return;
        }

        var devicesOutput = _lastCommandOutput;
        var mdnsEndpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (RunAdbCommand("mdns services"))
        {
            var serviceLines = _lastCommandOutput.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in serviceLines)
            {
                var columns = Regex.Split(line.Trim(), @"\s+");
                if (columns.Length < 3 ||
                    !string.Equals(columns[1], "_adb-tls-connect._tcp.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                mdnsEndpoints[columns[0] + columns[1]] = columns[2];
            }
        }

        foreach (var device in _managedDevices)
            device.IsConnected = false;

        var lines = devicesOutput.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var columns = Regex.Split(line.Trim(), @"\s+");
            if (columns.Length < 2 || !string.Equals(columns[1], "device", StringComparison.OrdinalIgnoreCase))
                continue;

            var serial = columns[0];
            string endpoint;
            if (!mdnsEndpoints.TryGetValue(serial, out endpoint))
            {
                if (!serial.Contains(":"))
                    continue;
                endpoint = serial;
            }

            var displayName = serial;
            for (var i = 2; i < columns.Length; i++)
            {
                if (!columns[i].StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                    continue;

                displayName = columns[i].Substring("model:".Length).Replace('_', ' ');
                break;
            }

            var managedDevice = _managedDevices.Find(item =>
                string.Equals(item.Serial, serial, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
            if (managedDevice == null)
            {
                managedDevice = new WirelessAdbDevice();
                _managedDevices.Add(managedDevice);
            }

            managedDevice.Serial = serial;
            managedDevice.Endpoint = endpoint;
            managedDevice.DisplayName = displayName;
            managedDevice.IsConnected = true;
        }

        SaveManagedDevices();
        Repaint();
    }

    private void LoadManagedDevices()
    {
        var json = EditorPrefs.GetString(DeviceListKey, string.Empty);
        if (string.IsNullOrEmpty(json))
            return;

        var savedDevices = JsonUtility.FromJson<WirelessAdbDeviceList>(json);
        if (savedDevices == null || savedDevices.Devices == null)
            return;

        _managedDevices.Clear();
        foreach (var device in savedDevices.Devices)
        {
            if (device == null || string.IsNullOrEmpty(device.Endpoint))
                continue;
            device.IsConnected = false;
            _managedDevices.Add(device);
        }
    }

    private void SaveManagedDevices()
    {
        EditorPrefs.SetString(
            DeviceListKey,
            JsonUtility.ToJson(new WirelessAdbDeviceList { Devices = _managedDevices }));
    }

    private bool IsAdbAvailable()
    {
        return !string.IsNullOrWhiteSpace(_adbPath) && File.Exists(_adbPath);
    }

    private bool IsEndpointValid(string port)
    {
        return IsValidHost(_host) && ushort.TryParse(port, out var portNumber) && portNumber > 0;
    }

    private static bool IsValidHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.IndexOfAny(new[] { ' ', '/', '\\', '"' }) >= 0)
            return false;

        if (IPAddress.TryParse(host, out _))
            return true;

        return Regex.IsMatch(host, @"^[A-Za-z0-9][A-Za-z0-9.-]*[A-Za-z0-9]$") ||
               Regex.IsMatch(host, @"^[A-Za-z0-9]$");
    }

    private string BuildEndpoint(string port)
    {
        var host = _host;
        if (IPAddress.TryParse(host, out var address) &&
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            host = "[" + host + "]";
        }

        return host + ":" + port;
    }

    private static string Quote(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string FindAdbPath()
    {
        var sdkRoots = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            EditorPrefs.GetString("AndroidSdkRoot", string.Empty),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
            Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines", "AndroidPlayer", "SDK")
        };

        foreach (var sdkRoot in sdkRoots)
        {
            if (string.IsNullOrWhiteSpace(sdkRoot))
                continue;

            var candidate = Path.Combine(sdkRoot, "platform-tools", GetAdbFileName());
            if (File.Exists(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private static string GetInitialDirectory()
    {
        return string.IsNullOrEmpty(_staticAdbDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : _staticAdbDirectory;
    }

    private static string _staticAdbDirectory
    {
        get
        {
            var savedPath = EditorPrefs.GetString(AdbPathKey, string.Empty);
            return File.Exists(savedPath) ? Path.GetDirectoryName(savedPath) : string.Empty;
        }
    }

    private static string GetAdbFileName()
    {
        return Application.platform == RuntimePlatform.WindowsEditor ? "adb.exe" : "adb";
    }

    private enum DeviceAction
    {
        None,
        Disconnect,
        Reconnect,
        Remove
    }

    [Serializable]
    private sealed class WirelessAdbDevice
    {
        public string Serial;
        public string Endpoint;
        public string DisplayName;
        public bool IsConnected;
    }

    [Serializable]
    private sealed class WirelessAdbDeviceList
    {
        public List<WirelessAdbDevice> Devices = new List<WirelessAdbDevice>();
    }
}

internal sealed class AdbStatusPopup : EditorWindow
{
    internal enum Status
    {
        Success,
        Warning,
        Error
    }

    private string _message;
    private Status _status;
    private string _confirmLabel;
    private string _cancelLabel;
    private bool _confirmed;

    internal static void Show(string title, string message, Status status)
    {
        Open(title, message, status, "OK", null);
    }

    internal static bool Confirm(
        string title,
        string message,
        string confirmLabel,
        string cancelLabel,
        Status status)
    {
        var popup = Open(title, message, status, confirmLabel, cancelLabel);
        return popup._confirmed;
    }

    private static AdbStatusPopup Open(
        string title,
        string message,
        Status status,
        string confirmLabel,
        string cancelLabel)
    {
        var popup = CreateInstance<AdbStatusPopup>();
        popup.titleContent = new GUIContent(title);
        popup._message = message;
        popup._status = status;
        popup._confirmLabel = confirmLabel;
        popup._cancelLabel = cancelLabel;
        popup.minSize = new Vector2(400f, 170f);
        popup.maxSize = new Vector2(620f, 400f);
        var mainWindow = EditorGUIUtility.GetMainWindowPosition();
        var width = popup.minSize.x;
        var height = popup.minSize.y;
        popup.position = new Rect(
            mainWindow.x + (mainWindow.width - width) * 0.5f,
            mainWindow.y + (mainWindow.height - height) * 0.5f,
            width,
            height);
        popup.ShowModalUtility();
        return popup;
    }

    private void OnGUI()
    {
        var bannerRect = EditorGUILayout.GetControlRect(false, 32f);
        EditorGUI.DrawRect(bannerRect, GetStatusColor(_status));
        var oldColor = GUI.contentColor;
        GUI.contentColor = EditorGUIUtility.isProSkin ? Color.white : Color.black;
        GUI.Label(
            new Rect(bannerRect.x + 12f, bannerRect.y, bannerRect.width - 24f, bannerRect.height),
            GetStatusLabel(_status),
            EditorStyles.boldLabel);
        GUI.contentColor = oldColor;

        EditorGUILayout.Space(8f);
        EditorGUILayout.HelpBox(_message, GetMessageType(_status));
        GUILayout.FlexibleSpace();

        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (_cancelLabel != null && GUILayout.Button(_cancelLabel, GUILayout.Width(100f)))
            Close();

        if (GUILayout.Button(_confirmLabel, GUILayout.Width(100f)))
        {
            _confirmed = true;
            Close();
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(8f);
    }

    private static Color GetStatusColor(Status status)
    {
        switch (status)
        {
            case Status.Success:
                return new Color(0.20f, 0.55f, 0.30f);
            case Status.Warning:
                return new Color(0.72f, 0.48f, 0.12f);
            default:
                return new Color(0.70f, 0.22f, 0.20f);
        }
    }

    private static string GetStatusLabel(Status status)
    {
        switch (status)
        {
            case Status.Success:
                return "SUCCESS";
            case Status.Warning:
                return "NEEDS ATTENTION";
            default:
                return "ERROR";
        }
    }

    private static MessageType GetMessageType(Status status)
    {
        switch (status)
        {
            case Status.Success:
                return MessageType.Info;
            case Status.Warning:
                return MessageType.Warning;
            default:
                return MessageType.Error;
        }
    }
}
