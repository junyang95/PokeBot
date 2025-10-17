using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SysBot.Base;
using System.Diagnostics;
using SysBot.Pokemon.Helpers;
using SysBot.Pokemon.WinForms.WebApi;
using static SysBot.Pokemon.WinForms.WebApi.RestartManager;
using System.Collections.Concurrent;

namespace SysBot.Pokemon.WinForms;

public static class WebApiExtensions
{
    private static BotServer? _server;
    private static TcpListener? _tcp;
    private static CancellationTokenSource? _cts;
    private static CancellationTokenSource? _monitorCts;
    private static Main? _main;

    private static int _webPort = 8080; // Will be set from config
    private static int _tcpPort = 0;
    private static readonly object _portLock = new object();
    private static readonly ConcurrentDictionary<int, DateTime> _portReservations = new();

    public static void InitWebServer(this Main mainForm)
    {
        _main = mainForm;
        _cts = new CancellationTokenSource(); // Initialize early for background tasks

        // Get the configured port from settings
        if (mainForm.Config?.Hub?.WebServer != null)
        {
            _webPort = mainForm.Config.Hub.WebServer.ControlPanelPort;
            
            // Validate port range
            if (_webPort < 1 || _webPort > 65535)
            {
                LogUtil.LogError($"Invalid web server port {_webPort}. Using default port 8080.", "WebServer");
                _webPort = 8080;
            }
            
            // Update the UpdateManager with the configured port
            UpdateManager.SetConfiguredWebPort(_webPort);
            
            // Check if web server is enabled
            if (!mainForm.Config.Hub.WebServer.EnableWebServer)
            {
                LogUtil.LogInfo("Web Control Panel is disabled in settings.", "WebServer");
                return;
            }
            
            LogUtil.LogInfo($"Web Control Panel will be hosted on port {_webPort}", "WebServer");
        }
        else
        {
            // No config available, use default and update UpdateManager
            UpdateManager.SetConfiguredWebPort(_webPort);
        }

        try
        {
            CleanupStalePortFiles();

            CheckPostRestartStartup(mainForm);

            if (IsPortInUse(_webPort))
            {
                lock (_portLock)
                {
                    _tcpPort = FindAvailablePort(8081);
                    ReservePort(_tcpPort);
                }
                StartTcpOnly();

                StartMasterMonitor();
                RestartManager.Initialize(mainForm, _tcpPort);
                // Check for any pending update state and attempt to resume
                _ = Task.Run(async () =>
                {
                    await Task.Delay(10000); // Wait for system to stabilize
                    var currentState = UpdateManager.GetCurrentState();
                    if (currentState != null && !currentState.IsComplete)
                    {
                        LogUtil.LogInfo($"Found incomplete update session {currentState.SessionId}, attempting to resume", "WebServer");
                        await UpdateManager.StartOrResumeUpdateAsync(mainForm, _tcpPort);
                    }
                });
                
                return;
            }

            TryAddUrlReservation(_webPort);

            lock (_portLock)
            {
                _tcpPort = FindAvailablePort(8081);
                ReservePort(_tcpPort);
            }
            StartFullServer();

            RestartManager.Initialize(mainForm, _tcpPort);
            // Check for any pending update state and attempt to resume
            _ = Task.Run(async () =>
            {
                await Task.Delay(10000); // Wait for system to stabilize
                var currentState = UpdateManager.GetCurrentState();
                if (currentState != null && !currentState.IsComplete)
                {
                    LogUtil.LogInfo($"Found incomplete update session {currentState.SessionId}, attempting to resume", "WebServer");
                    await UpdateManager.StartOrResumeUpdateAsync(mainForm, _tcpPort);
                }
            });
            
            // Periodically clean up completed update sessions
            _ = Task.Run(async () =>
            {
                while (_cts != null && !_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(30), _cts.Token);
                        UpdateManager.ClearState();
                    }
                    catch (OperationCanceledException)
                    {
                        break; // Exit gracefully when cancelled
                    }
                }
            });
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to initialize web server: {ex.Message}", "WebServer");
        }
    }

    private static void ReservePort(int port)
    {
        _portReservations[port] = DateTime.Now;
    }

    private static void ReleasePort(int port)
    {
        _portReservations.TryRemove(port, out _);
    }

    private static void CleanupStalePortFiles()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var exeDir = Path.GetDirectoryName(exePath) ?? Program.WorkingDirectory;

            // Also clean up stale port reservations (older than 5 minutes)
            var now = DateTime.Now;
            var staleReservations = _portReservations
                .Where(kvp => (now - kvp.Value).TotalMinutes > 5)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var port in staleReservations)
            {
                _portReservations.TryRemove(port, out _);
            }

            var portFiles = Directory.GetFiles(exeDir, "PokeBot_*.port");

            foreach (var portFile in portFiles)
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(portFile);
                    var pidStr = fileName.Substring("PokeBot_".Length);

                    if (int.TryParse(pidStr, out int pid))
                    {
                        if (pid == Environment.ProcessId)
                            continue;

                        try
                        {
                            using var process = Process.GetProcessById(pid);
                            if (process.ProcessName.Contains("SysBot", StringComparison.OrdinalIgnoreCase) ||
                                process.ProcessName.Contains("PokeBot", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                        }
                        catch (ArgumentException)
                        {
                        }

                        File.Delete(portFile);
                        LogUtil.LogInfo($"Cleaned up stale port file: {Path.GetFileName(portFile)}", "WebServer");
                    }
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Error processing port file {portFile}: {ex.Message}", "WebServer");
                }
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to cleanup stale port files: {ex.Message}", "WebServer");
        }
    }

    private static void StartMasterMonitor()
    {
        _monitorCts = new CancellationTokenSource();

        Task.Run(async () =>
        {
            var random = new Random();

            while (!_monitorCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(10000 + random.Next(5000), _monitorCts.Token);

                    if (UpdateManager.IsUpdateInProgress() || RestartManager.IsRestartInProgress)
                    {
                        continue;
                    }

                    if (!IsPortInUse(_webPort))
                    {
                        LogUtil.LogInfo("Master web server is down. Attempting to take over...", "WebServer");

                        await Task.Delay(random.Next(1000, 3000));

                        if (!IsPortInUse(_webPort) && !UpdateManager.IsUpdateInProgress() && !RestartManager.IsRestartInProgress)
                        {
                            TryTakeOverAsMaster();
                            break;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogUtil.LogError($"Error in master monitor: {ex.Message}", "WebServer");
                }
            }
        }, _monitorCts.Token);
    }

    private static void TryTakeOverAsMaster()
    {
        try
        {
            TryAddUrlReservation(_webPort);

            _server = new BotServer(_main!, _webPort, _tcpPort);
            _server.Start();

            _monitorCts?.Cancel();
            _monitorCts = null;

            LogUtil.LogInfo($"Successfully took over as master web server on port {_webPort}", "WebServer");
            LogUtil.LogInfo($"Web interface is now available at http://localhost:{_webPort}", "WebServer");
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to take over as master: {ex.Message}", "WebServer");
            StartMasterMonitor();
        }
    }

    private static bool TryAddUrlReservation(int port)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"http add urlacl url=http://+:{port}/ user=Everyone",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Verb = "runas"
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void StartTcpOnly()
    {
        StartTcp();
        
        // Slaves no longer need their own web server - logs are read directly from file by master
        
        CreatePortFile();
    }

    private static void StartFullServer()
    {
        try
        {
            _server = new BotServer(_main!, _webPort, _tcpPort);
            _server.Start();
            StartTcp();
            CreatePortFile();
        }
        catch (Exception ex) when (ex.Message.Contains("conflicts with an existing registration"))
        {
            // Another instance became master first - gracefully become a slave
            LogUtil.LogInfo($"Port {_webPort} conflict during startup, starting as slave", "WebServer");
            StartTcpOnly();  // This will create the port file as a slave
        }
    }

    private static void StartTcp()
    {
        _cts ??= new CancellationTokenSource(); // Only create if not already created
        Task.Run(() => StartTcpListenerAsync(_cts.Token));
    }
    
    private static async Task StartTcpListenerAsync(CancellationToken cancellationToken)
    {
        const int maxRetries = 5;
        var random = new Random();
        
        for (int retry = 0; retry < maxRetries && !cancellationToken.IsCancellationRequested; retry++)
        {
            try
            {
                _tcp = new TcpListener(System.Net.IPAddress.Loopback, _tcpPort);
                _tcp.Start();
                
                LogUtil.LogInfo($"TCP listener started successfully on port {_tcpPort}", "TCP");
                
                await AcceptClientsAsync(cancellationToken);
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && retry < maxRetries - 1)
            {
                LogUtil.LogInfo($"TCP port {_tcpPort} in use, finding new port (attempt {retry + 1}/{maxRetries})", "TCP");
                await Task.Delay(random.Next(500, 1500), cancellationToken);
                
                lock (_portLock)
                {
                    ReleasePort(_tcpPort);
                    _tcpPort = FindAvailablePort(_tcpPort + 1);
                    ReservePort(_tcpPort);
                }
                
                CreatePortFile();
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogUtil.LogError($"TCP listener error: {ex.Message}", "TCP");
                
                if (retry == maxRetries - 1)
                {
                    LogUtil.LogError($"Failed to start TCP listener after {maxRetries} attempts", "TCP");
                    throw new InvalidOperationException("Unable to find available TCP port");
                }
            }
        }
    }
    
    private static async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpTask = _tcp!.AcceptTcpClientAsync();
                var tcs = new TaskCompletionSource<bool>();
                
                using var registration = cancellationToken.Register(() => tcs.SetCanceled());
                var completedTask = await Task.WhenAny(tcpTask, tcs.Task);
                
                if (completedTask == tcpTask && tcpTask.IsCompletedSuccessfully)
                {
                    _ = HandleClientSafelyAsync(tcpTask.Result);
                }
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
    
    private static async Task HandleClientSafelyAsync(TcpClient client)
    {
        try
        {
            await HandleClient(client);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Unhandled error in HandleClient: {ex.Message}", "TCP");
        }
    }

    private static async Task HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var command = await reader.ReadLineAsync();
                if (!string.IsNullOrEmpty(command))
                {
                    var response = await ProcessCommandAsync(command);
                    await writer.WriteLineAsync(response);
                    await writer.FlushAsync();
                }
            }
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            // Normal disconnection - don't log as error
        }
        catch (ObjectDisposedException)
        {
            // Normal during shutdown
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error handling TCP client: {ex.Message}", "TCP");
        }
    }
    
    private static async Task<string> ProcessCommandAsync(string command)
    {
        return await Task.Run(() => ProcessCommand(command));
    }

    private static string ProcessCommand(string command)
    {
        if (_main == null)
            return "ERROR: Main form not initialized";

        var parts = command.Split(':');
        var cmd = parts[0].ToUpperInvariant();
        var botId = parts.Length > 1 ? parts[1] : null;

        return cmd switch
        {
            "STARTALL" => ExecuteGlobalCommand(BotControlCommand.Start),
            "STOPALL" => ExecuteGlobalCommand(BotControlCommand.Stop),
            "IDLEALL" => ExecuteGlobalCommand(BotControlCommand.Idle),
            "RESUMEALL" => ExecuteGlobalCommand(BotControlCommand.Resume),
            "RESTARTALL" => ExecuteGlobalCommand(BotControlCommand.Restart),
            "REBOOTALL" => ExecuteGlobalCommand(BotControlCommand.RebootAndStop),
            "SCREENONALL" => ExecuteGlobalCommand(BotControlCommand.ScreenOnAll),
            "SCREENOFFALL" => ExecuteGlobalCommand(BotControlCommand.ScreenOffAll),
            "LISTBOTS" => GetBotsList(),
            "STATUS" => GetBotStatuses(botId),
            "ISREADY" => CheckReady(),
            "INFO" => GetInstanceInfo(),
            "VERSION" => PokeBot.Version,
            "UPDATE" => TriggerUpdate(),
            "SELFRESTARTALL" => TriggerSelfRestart(),
            "RESTARTSCHEDULE" => GetRestartSchedule(),
            "REMOTE_BUTTON" => HandleRemoteButton(parts),
            "REMOTE_MACRO" => HandleRemoteMacro(parts),
            _ => $"ERROR: Unknown command '{cmd}'"
        };
    }

    private static volatile bool _updateInProgress = false;
    private static readonly object _updateLock = new();
    
    private static string TriggerUpdate()
    {
        try
        {
            lock (_updateLock)
            {
                if (_updateInProgress)
                {
                    LogUtil.LogInfo("Update already in progress - ignoring duplicate request", "WebApiExtensions");
                    return "Update already in progress";
                }
                _updateInProgress = true;
            }

            if (_main == null)
            {
                lock (_updateLock) { _updateInProgress = false; }
                return "ERROR: Main form not initialized";
            }

            LogUtil.LogInfo($"Update triggered for instance on port {_tcpPort}", "WebApiExtensions");

            _main.BeginInvoke((System.Windows.Forms.MethodInvoker)(async () =>
            {
                try
                {
                    var (updateAvailable, _, newVersion) = await UpdateChecker.CheckForUpdatesAsync(false);
                    if (updateAvailable || true) // Always allow update when triggered remotely
                    {
                        var updateForm = new UpdateForm(false, newVersion ?? "latest", true);
                        updateForm.PerformUpdate();
                    }
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Error during update: {ex.Message}", "WebApiExtensions");
                }
            }));

            return "OK: Update triggered";
        }
        catch (Exception ex)
        {
            lock (_updateLock) { _updateInProgress = false; }
            return $"ERROR: {ex.Message}";
        }
    }

    private static string TriggerSelfRestart()
    {
        try
        {
            if (_main == null)
                return "ERROR: Main form not initialized";

            Task.Run(async () =>
            {
                await Task.Delay(2000);
                _main.BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
                {
                    Application.Restart();
                }));
            });

            return "OK: Restart triggered";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    private static string GetRestartSchedule()
    {
        try
        {
            var config = RestartManager.GetScheduleConfig();
            var nextRestart = RestartManager.NextScheduledRestart;
            
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                config.Enabled,
                config.Time,
                NextRestart = nextRestart?.ToString("yyyy-MM-dd HH:mm:ss"),
                IsRestartInProgress = RestartManager.IsRestartInProgress,
                CurrentState = RestartManager.CurrentState.ToString()
            });
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    private static string ExecuteGlobalCommand(BotControlCommand command)
    {
        try
        {
            ExecuteMainFormMethod("SendAll", command);
            return $"OK: {command} command sent to all bots";
        }
        catch (Exception ex)
        {
            return $"ERROR: Failed to execute {command} - {ex.Message}";
        }
    }
    
    private static void ExecuteMainFormMethod(string methodName, params object[] args)
    {
        _main!.BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
        {
            var method = _main.GetType().GetMethod(methodName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(_main, args);
        }));
    }

    private static string GetBotsList()
    {
        try
        {
            var botList = new List<object>();
            var config = GetConfig();
            var controllers = GetBotControllers();

            if (controllers.Count == 0)
            {
                var botsProperty = _main!.GetType().GetProperty("Bots",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (botsProperty?.GetValue(_main) is List<PokeBotState> bots)
                {
                    foreach (var bot in bots)
                    {
                        botList.Add(new
                        {
                            Id = $"{bot.Connection.IP}:{bot.Connection.Port}",
                            Name = bot.Connection.IP,
                            RoutineType = bot.InitialRoutine.ToString(),
                            Status = "Unknown",
                            ConnectionType = bot.Connection.Protocol.ToString(),
                            bot.Connection.IP,
                            bot.Connection.Port
                        });
                    }

                    return System.Text.Json.JsonSerializer.Serialize(new { Bots = botList });
                }
            }

            foreach (var controller in controllers)
            {
                var state = controller.State;
                var botName = GetBotName(state, config);
                var status = controller.ReadBotState();

                botList.Add(new
                {
                    Id = $"{state.Connection.IP}:{state.Connection.Port}",
                    Name = botName,
                    RoutineType = state.InitialRoutine.ToString(),
                    Status = status,
                    ConnectionType = state.Connection.Protocol.ToString(),
                    state.Connection.IP,
                    state.Connection.Port
                });
            }

            return System.Text.Json.JsonSerializer.Serialize(new { Bots = botList });
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"GetBotsList error: {ex.Message}", "WebAPI");
            return $"ERROR: Failed to get bots list - {ex.Message}";
        }
    }

    private static string GetBotStatuses(string? botId)
    {
        try
        {
            var config = GetConfig();
            var controllers = GetBotControllers();

            if (string.IsNullOrEmpty(botId))
            {
                var statuses = controllers.Select(c => new
                {
                    Id = $"{c.State.Connection.IP}:{c.State.Connection.Port}",
                    Name = GetBotName(c.State, config),
                    Status = c.ReadBotState()
                }).ToList();

                return System.Text.Json.JsonSerializer.Serialize(statuses);
            }

            var botController = controllers.FirstOrDefault(c =>
                $"{c.State.Connection.IP}:{c.State.Connection.Port}" == botId);

            return botController?.ReadBotState() ?? "ERROR: Bot not found";
        }
        catch (Exception ex)
        {
            return $"ERROR: Failed to get status - {ex.Message}";
        }
    }

    private static string CheckReady()
    {
        try
        {
            var controllers = GetBotControllers();
            var hasRunningBots = controllers.Any(c => c.GetBot()?.IsRunning ?? false);
            return hasRunningBots ? "READY" : "NOT_READY";
        }
        catch
        {
            return "NOT_READY";
        }
    }

    private static string GetInstanceInfo()
    {
        try
        {
            var config = GetConfig();
            var version = GetVersion();
            var mode = config?.Mode.ToString() ?? "Unknown";
            var name = GetInstanceName(config, mode);

            var info = new
            {
                Version = version,
                Mode = mode,
                Name = name,
                Environment.ProcessId,
                Port = _tcpPort,
                ProcessPath = Environment.ProcessPath
            };

            return System.Text.Json.JsonSerializer.Serialize(info);
        }
        catch (Exception ex)
        {
            return $"ERROR: Failed to get instance info - {ex.Message}";
        }
    }
    
    private static string HandleRemoteButton(string[] parts)
    {
        try
        {
            if (parts.Length < 3)
                return "ERROR: Invalid command format. Expected REMOTE_BUTTON:button:botIndex";
                
            var button = parts[1];
            if (!int.TryParse(parts[2], out var botIndex))
                return "ERROR: Invalid bot index";
                
            var controllers = GetBotControllers();
            if (botIndex < 0 || botIndex >= controllers.Count)
                return $"ERROR: Bot index {botIndex} out of range";
                
            var botController = controllers[botIndex];
            var botSource = botController.GetBot();
            
            if (botSource?.Bot == null)
                return $"ERROR: Bot at index {botIndex} not available";
                
            if (!botSource.IsRunning)
                return $"ERROR: Bot at index {botIndex} is not running";
                
            var bot = botSource.Bot;
            if (bot.Connection is not ISwitchConnectionAsync connection)
                return "ERROR: Bot connection not available";
            
            var switchButton = MapButtonToSwitch(button);
            if (switchButton == null)
                return $"ERROR: Invalid button: {button}";
            
            var cmd = SwitchCommand.Click(switchButton.Value);
            
            // Execute the command synchronously since we're already in a background thread
            Task.Run(async () => await connection.SendAsync(cmd, CancellationToken.None)).Wait();
            
            return $"OK: Button {button} pressed on bot {botIndex}";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }
    
    private static string HandleRemoteMacro(string[] parts)
    {
        try
        {
            if (parts.Length < 3)
                return "ERROR: Invalid command format. Expected REMOTE_MACRO:macro:botIndex";
                
            var macro = parts[1];
            if (!int.TryParse(parts[2], out var botIndex))
                return "ERROR: Invalid bot index";
                
            var controllers = GetBotControllers();
            if (botIndex < 0 || botIndex >= controllers.Count)
                return $"ERROR: Bot index {botIndex} out of range";
                
            var botController = controllers[botIndex];
            var botSource = botController.GetBot();
            
            if (botSource?.Bot == null)
                return $"ERROR: Bot at index {botIndex} not available";
                
            if (!botSource.IsRunning)
                return $"ERROR: Bot at index {botIndex} is not running";
                
            // For now, just return success - macro implementation can be added later
            return $"OK: Macro {macro} executed on bot {botIndex}";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }
    
    private static SwitchButton? MapButtonToSwitch(string button)
    {
        return button.ToUpperInvariant() switch
        {
            "A" => SwitchButton.A,
            "B" => SwitchButton.B,
            "X" => SwitchButton.X,
            "Y" => SwitchButton.Y,
            "UP" => SwitchButton.DUP,
            "DOWN" => SwitchButton.DDOWN,
            "LEFT" => SwitchButton.DLEFT,
            "RIGHT" => SwitchButton.DRIGHT,
            "L" => SwitchButton.L,
            "R" => SwitchButton.R,
            "ZL" => SwitchButton.ZL,
            "ZR" => SwitchButton.ZR,
            "LSTICK" => SwitchButton.LSTICK,
            "RSTICK" => SwitchButton.RSTICK,
            "HOME" => SwitchButton.HOME,
            "CAPTURE" => SwitchButton.CAPTURE,
            "PLUS" => SwitchButton.PLUS,
            "MINUS" => SwitchButton.MINUS,
            _ => null
        };
    }

    private static List<BotController> GetBotControllers()
    {
        var flpBotsField = _main!.GetType().GetField("FLP_Bots",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (flpBotsField?.GetValue(_main) is FlowLayoutPanel flpBots)
        {
            return [.. flpBots.Controls.OfType<BotController>()];
        }

        return [];
    }

    private static ProgramConfig? GetConfig()
    {
        var configProp = _main?.GetType().GetProperty("Config",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return configProp?.GetValue(_main) as ProgramConfig;
    }

    private static string GetBotName(PokeBotState state, ProgramConfig? config)
    {
        return state.Connection.IP;
    }

    private static string GetVersion()
    {
        return PokeBot.Version;
    }

    private static string GetInstanceName(ProgramConfig? config, string mode)
    {
        if (!string.IsNullOrEmpty(config?.Hub?.BotName))
            return config.Hub.BotName;

        return mode switch
        {
            "LGPE" => "LGPE",
            "BDSP" => "BDSP",
            "SWSH" => "SWSH",
            "SV" => "SV",
            "LA" => "LA",
            _ => "PokeBot"
        };
    }

    private static void CreatePortFile()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var exeDir = Path.GetDirectoryName(exePath) ?? Program.WorkingDirectory;
            var portFile = Path.Combine(exeDir, $"PokeBot_{Environment.ProcessId}.port");
            var tempFile = portFile + ".tmp";

            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs))
            {
                writer.WriteLine(_tcpPort);
                // No longer writing web port - slaves don't have web servers
                writer.Flush();
                fs.Flush(true);
            }

            File.Move(tempFile, portFile, true);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to create port file: {ex.Message}", "WebServer");
        }
    }

    private static void CleanupPortFile()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var exeDir = Path.GetDirectoryName(exePath) ?? Program.WorkingDirectory;
            var portFile = Path.Combine(exeDir, $"PokeBot_{Environment.ProcessId}.port");

            if (File.Exists(portFile))
                File.Delete(portFile);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to cleanup port file: {ex.Message}", "WebServer");
        }
    }

    private static int FindAvailablePort(int startPort)
    {
        var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
        var exeDir = Path.GetDirectoryName(exePath) ?? Program.WorkingDirectory;

        // Use a lock to prevent race conditions
        lock (_portLock)
        {
            for (int port = startPort; port < startPort + 100; port++)
            {
                // Check if port is reserved by another instance
                if (_portReservations.ContainsKey(port))
                    continue;

                if (!IsPortInUse(port))
                {
                    // Check if any port file claims this port
                    var portFiles = Directory.GetFiles(exeDir, "PokeBot_*.port");
                    bool portClaimed = false;

                    foreach (var file in portFiles)
                    {
                        try
                        {
                            // Lock the file before reading to prevent race conditions
                            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                            using var reader = new StreamReader(fs);
                            var content = reader.ReadToEnd().Trim();
                            if (content == port.ToString() || content.Contains($"\"Port\":{port}"))
                            {
                                portClaimed = true;
                                break;
                            }
                        }
                        catch { }
                    }

                    if (!portClaimed)
                    {
                        // Double-check the port is still available
                        if (!IsPortInUse(port))
                        {
                            return port;
                        }
                    }
                }
            }
        }
        throw new InvalidOperationException("No available ports found");
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
            var response = client.GetAsync($"http://localhost:{port}/api/bot/instances").Result;
            return response.IsSuccessStatusCode;
        }
        catch
        {
            try
            {
                using var tcpClient = new TcpClient();
                var result = tcpClient.BeginConnect("127.0.0.1", port, null, null);
                var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(200));
                if (success)
                {
                    tcpClient.EndConnect(result);
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void StopWebServer(this Main mainForm)
    {
        try
        {
            _monitorCts?.Cancel();
            _cts?.Cancel();
            _tcp?.Stop();
            _server?.Dispose();
            RestartManager.Shutdown();

            // Release the port reservations
            lock (_portLock)
            {
                ReleasePort(_tcpPort);
            }

            CleanupPortFile();
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error stopping web server: {ex.Message}", "WebServer");
        }
    }

    private static void CheckPostRestartStartup(Main mainForm)
    {
        try
        {
            var workingDir = Path.GetDirectoryName(Application.ExecutablePath) ?? Environment.CurrentDirectory;
            var restartFlagPath = Path.Combine(workingDir, "restart_in_progress.flag");
            var updateFlagPath = Path.Combine(workingDir, "update_in_progress.flag");

            bool isPostRestart = File.Exists(restartFlagPath);
            bool isPostUpdate = File.Exists(updateFlagPath);

            if (!isPostRestart && !isPostUpdate)
                return;

            string operation = isPostRestart ? "restart" : "update";
            string logSource = isPostRestart ? "RestartManager" : "UpdateManager";
            
            LogUtil.LogInfo($"Post-{operation} startup detected. Waiting for all instances to come online...", logSource);

            if (isPostRestart) File.Delete(restartFlagPath);
            if (isPostUpdate) File.Delete(updateFlagPath);

            Task.Run(() => HandlePostOperationStartupAsync(mainForm, operation, logSource));
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error checking post-restart/update startup: {ex.Message}", "StartupManager");
        }
    }
    
    private static async Task HandlePostOperationStartupAsync(Main mainForm, string operation, string logSource)
    {
        await Task.Delay(5000);
        
        const int maxAttempts = 12;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                LogUtil.LogInfo($"Post-{operation} check attempt {attempt + 1}/{maxAttempts}", logSource);
                
                // Start local bots
                ExecuteMainFormMethod("SendAll", BotControlCommand.Start);
                LogUtil.LogInfo("Start All command sent to local bots", logSource);
                
                // Start remote instances
                var instances = GetAllRunningInstances(0);
                if (instances.Count > 0)
                {
                    LogUtil.LogInfo($"Found {instances.Count} remote instances online. Sending Start All command...", logSource);
                    await SendStartCommandsToRemoteInstancesAsync(instances, logSource);
                }
                
                LogUtil.LogInfo($"Post-{operation} Start All commands completed successfully", logSource);
                break;
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Error during post-{operation} startup attempt {attempt + 1}: {ex.Message}", logSource);
                if (attempt < maxAttempts - 1)
                    await Task.Delay(5000);
            }
        }
    }
    
    private static async Task SendStartCommandsToRemoteInstancesAsync(List<(int Port, int ProcessId)> instances, string logSource)
    {
        var tasks = instances.Select(instance => Task.Run(() =>
        {
            try
            {
                var response = BotServer.QueryRemote(instance.Port, "STARTALL");
                LogUtil.LogInfo($"Start command sent to port {instance.Port}: {response}", logSource);
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Failed to send start command to port {instance.Port}: {ex.Message}", logSource);
            }
        }));
        
        await Task.WhenAll(tasks);
    }

    private static List<(int Port, int ProcessId)> GetAllRunningInstances(int currentPort)
    {
        var instances = new List<(int, int)>();

        try
        {
            var processes = Process.GetProcessesByName("PokeBot")
                .Where(p => p.Id != Environment.ProcessId);

            foreach (var process in processes)
            {
                try
                {
                    var exePath = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(exePath))
                        continue;

                    var portFile = Path.Combine(Path.GetDirectoryName(exePath)!, $"PokeBot_{process.Id}.port");
                    if (!File.Exists(portFile))
                        continue;

                    var portText = File.ReadAllText(portFile).Trim();
                    // Port file now contains TCP port on first line, web port on second line (for slaves)
                    var lines = portText.Split('\n', '\r').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                    if (lines.Length == 0 || !int.TryParse(lines[0], out var port))
                        continue;

                    if (IsPortInUse(port))
                    {
                        instances.Add((port, process.Id));
                    }
                }
                catch { }
            }
        }
        catch { }

        return instances;
    }

}
