﻿using System;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Threading;
using Prometheus;
using Robust.Server.Console;
using Robust.Server.Interfaces;
using Robust.Server.Interfaces.Console;
using Robust.Server.Interfaces.GameObjects;
using Robust.Server.Interfaces.GameState;
using Robust.Server.Interfaces.Placement;
using Robust.Server.Interfaces.Player;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Interfaces.Configuration;
using Robust.Shared.Interfaces.GameObjects;
using Robust.Shared.Interfaces.Map;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.Interfaces.Serialization;
using Robust.Shared.Interfaces.Timing;
using Robust.Shared.Interfaces.Timers;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Prototypes;
using Robust.Server.Interfaces.ServerStatus;
using Robust.Server.ViewVariables;
using Robust.Shared.Asynchronous;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.Shared.Interfaces.Log;
using Robust.Shared.Interfaces.Resources;
using Robust.Shared.Exceptions;
using Robust.Server.Interfaces.Debugging;
using Robust.Server.Scripting;
using Robust.Server.ServerStatus;
using Robust.Shared;
using Robust.Shared.Network.Messages;
using Robust.Server.DataMetrics;
using Robust.Server.Interfaces.Maps;
using Robust.Shared.Serialization;
using Stopwatch = Robust.Shared.Timing.Stopwatch;

namespace Robust.Server
{
    /// <summary>
    /// The master class that runs the rest of the engine.
    /// </summary>
    internal sealed class BaseServer : IBaseServerInternal
    {
        private static readonly Gauge ServerUpTime = Metrics.CreateGauge(
            "robust_server_uptime",
            "The real time the server main loop has been running.");

        private static readonly Gauge ServerCurTime = Metrics.CreateGauge(
            "robust_server_curtime",
            "The IGameTiming.CurTime of the server.");

        private static readonly Gauge ServerCurTick = Metrics.CreateGauge(
            "robust_server_curtick",
            "The IGameTiming.CurTick of the server.");


        [Dependency] private readonly IConfigurationManager _config = default!;
        [Dependency] private readonly IComponentManager _components = default!;
        [Dependency] private readonly IServerEntityManager _entities = default!;
        [Dependency] private readonly ILogManager _log = default!;
        [Dependency] private readonly IRobustSerializer _serializer = default!;
        [Dependency] private readonly IGameTiming _time = default!;
        [Dependency] private readonly IResourceManagerInternal _resources = default!;
        [Dependency] private readonly IMapManager _mapManager = default!;
        [Dependency] private readonly ITimerManager timerManager = default!;
        [Dependency] private readonly IServerGameStateManager _stateManager = default!;
        [Dependency] private readonly IServerNetManager _network = default!;
        [Dependency] private readonly ISystemConsoleManager _systemConsole = default!;
        [Dependency] private readonly ITaskManager _taskManager = default!;
        [Dependency] private readonly IRuntimeLog runtimeLog = default!;
        [Dependency] private readonly IModLoader _modLoader = default!;
        [Dependency] private readonly IWatchdogApi _watchdogApi = default!;
        [Dependency] private readonly IScriptHost _scriptHost = default!;
        [Dependency] private readonly IMetricsManager _metricsManager = default!;

        private readonly Stopwatch _uptimeStopwatch = new Stopwatch();

        private CommandLineArgs _commandLineArgs = default!;
        private Func<ILogHandler>? _logHandlerFactory;
        private ILogHandler? _logHandler;
        private IGameLoop _mainLoop = default!;

        private TimeSpan _lastTitleUpdate;
        private int _lastReceivedBytes;
        private int _lastSentBytes;

        private string? _shutdownReason;

        private readonly ManualResetEventSlim _shutdownEvent = new ManualResetEventSlim(false);

        /// <inheritdoc />
        public int MaxPlayers => _config.GetCVar<int>("game.maxplayers");

        /// <inheritdoc />
        public string ServerName => _config.GetCVar<string>("game.hostname");

        /// <inheritdoc />
        public void Restart()
        {
            Logger.InfoS("srv", "Restarting Server...");

            Cleanup();
            Start(_logHandlerFactory);
        }

        /// <inheritdoc />
        public void Shutdown(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                Logger.InfoS("srv", "Shutting down...");
            else
                Logger.InfoS("srv", $"{reason}, shutting down...");

            _shutdownReason = reason;

            _mainLoop.Running = false;
            if (_logHandler != null)
            {
                _log.RootSawmill.RemoveHandler(_logHandler);
                (_logHandler as IDisposable)?.Dispose();
            }
        }

        public void SetCommandLineArgs(CommandLineArgs args)
        {
            _commandLineArgs = args;
        }

        /// <inheritdoc />
        public bool Start(Func<ILogHandler>? logHandlerFactory = null)
        {
            // Sets up the configMgr
            // If a config file path was passed, use it literally.
            // This ensures it's working-directory relative
            // (for people passing config file through the terminal or something).
            // Otherwise use the one next to the executable.
            if (_commandLineArgs?.ConfigFile != null)
            {
                _config.LoadFromFile(_commandLineArgs.ConfigFile);
            }
            else
            {
                var path = PathHelpers.ExecutableRelativeFile("server_config.toml");
                if (File.Exists(path))
                {
                    _config.LoadFromFile(path);
                }
                else
                {
                    _config.SetSaveFile(path);
                }
            }

            _config.OverrideConVars(EnvironmentVariables.GetEnvironmentCVars());

            if (_commandLineArgs != null)
            {
                _config.OverrideConVars(_commandLineArgs.CVars);
            }


            //Sets up Logging
            _config.RegisterCVar("log.enabled", true, CVar.ARCHIVE);
            _config.RegisterCVar("log.path", "logs", CVar.ARCHIVE);
            _config.RegisterCVar("log.format", "log_%(date)s-T%(time)s.txt", CVar.ARCHIVE);
            _config.RegisterCVar("log.level", LogLevel.Info, CVar.ARCHIVE);

            _logHandlerFactory = logHandlerFactory;

            var logHandler = logHandlerFactory?.Invoke() ?? null;

            var logEnabled = _config.GetCVar<bool>("log.enabled");

            if (logEnabled && logHandler == null)
            {
                var logPath = _config.GetCVar<string>("log.path");
                var logFormat = _config.GetCVar<string>("log.format");
                var logFilename = logFormat.Replace("%(date)s", DateTime.Now.ToString("yyyy-MM-dd"))
                    .Replace("%(time)s", DateTime.Now.ToString("hh-mm-ss"));
                var fullPath = Path.Combine(logPath, logFilename);

                if (!Path.IsPathRooted(fullPath))
                {
                    logPath = PathHelpers.ExecutableRelativeFile(fullPath);
                }

                logHandler = new FileLogHandler(logPath);
            }

            _log.RootSawmill.Level = _config.GetCVar<LogLevel>("log.level");

            if (logEnabled && logHandler != null)
            {
                _logHandler = logHandler;
                _log.RootSawmill.AddHandler(_logHandler!);
            }

            // Has to be done early because this guy's in charge of the main thread Synchronization Context.
            _taskManager.Initialize();

            LoadSettings();

            // Load metrics really early so that we can profile startup times in the future maybe.
            _metricsManager.Initialize();

            var netMan = IoCManager.Resolve<IServerNetManager>();
            try
            {
                netMan.Initialize(true);
                netMan.StartServer();
                netMan.RegisterNetMessage<MsgSetTickRate>(MsgSetTickRate.NAME);
            }
            catch (Exception e)
            {
                var port = netMan.Port;
                Logger.Fatal(
                    "Unable to setup networking manager. Check port {0} is not already in use and that all binding addresses are correct!\n{1}",
                    port, e);
                return true;
            }

            var dataDir = _commandLineArgs?.DataDir ?? PathHelpers.ExecutableRelativeFile("data");

            // Set up the VFS
            _resources.Initialize(dataDir);

#if FULL_RELEASE
            _resources.MountContentDirectory(@"./Resources/");
#else
            // Load from the resources dir in the repo root instead.
            // It's a debug build so this is fine.
            var contentRootDir = ProgramShared.FindContentRootDir();
            _resources.MountContentDirectory($@"{contentRootDir}RobustToolbox/Resources/");
            _resources.MountContentDirectory($@"{contentRootDir}bin/Content.Server/", new ResourcePath("/Assemblies/"));
            _resources.MountContentDirectory($@"{contentRootDir}Resources/");
#endif

            _modLoader.SetUseLoadContext(!DisableLoadContext);

            //identical code in game controller for client
            if (!_modLoader.TryLoadAssembly<GameShared>(_resources, $"Content.Shared"))
            {
                Logger.FatalS("eng", "Could not load any Shared DLL.");
                return true;
            }

            if (!_modLoader.TryLoadAssembly<GameServer>(_resources, $"Content.Server"))
            {
                Logger.FatalS("eng", "Could not load any Server DLL.");
                return true;
            }

            GCSettings.LatencyMode = GCLatencyMode.LowLatency;

            JitAhead.Start();

            //JitAhead.Thread.Join();

            _modLoader.BroadcastRunLevel(ModRunLevel.PreInit);

            // HAS to happen after content gets loaded.
            // Else the content types won't be included.
            // TODO: solve this properly.
            _serializer.Initialize();

            //IoCManager.Resolve<IMapLoader>().LoadedMapData +=
            //    IoCManager.Resolve<IRobustMappedStringSerializer>().AddStrings;
            IoCManager.Resolve<IPrototypeManager>().LoadedData +=
                IoCManager.Resolve<IRobustMappedStringSerializer>().AddStrings;

            // Initialize Tier 2 services
            IoCManager.Resolve<IGameTiming>().InSimulation = true;

            _stateManager.Initialize();
            IoCManager.Resolve<IPlayerManager>().Initialize(MaxPlayers);
            _mapManager.Initialize();
            _mapManager.Startup();
            IoCManager.Resolve<IPlacementManager>().Initialize();
            IoCManager.Resolve<IViewVariablesHost>().Initialize();
            IoCManager.Resolve<IDebugDrawingManager>().Initialize();

            // Call Init in game assemblies.
            _modLoader.BroadcastRunLevel(ModRunLevel.Init);

            _entities.Initialize();

            // because of 'reasons' this has to be called after the last assembly is loaded
            // otherwise the prototypes will be cleared
            var prototypeManager = IoCManager.Resolve<IPrototypeManager>();
            prototypeManager.LoadDirectory(new ResourcePath(@"/Prototypes"));
            prototypeManager.Resync();

            IoCManager.Resolve<IConsoleShell>().Initialize();
            IoCManager.Resolve<IConGroupController>().Initialize();
            _entities.Startup();
            _scriptHost.Initialize();

            _modLoader.BroadcastRunLevel(ModRunLevel.PostInit);

            IoCManager.Resolve<IStatusHost>().Start();

            AppDomain.CurrentDomain.ProcessExit += ProcessExiting;

            _watchdogApi.Initialize();

            return false;
        }

        private void ProcessExiting(object? sender, EventArgs e)
        {
            _taskManager.RunOnMainThread(() => Shutdown("ProcessExited"));
            // Give the server 10 seconds to shut down.
            // If it still hasn't managed to assume it's stuck or something.
            if (!_shutdownEvent.Wait(10_000))
            {
                System.Console.WriteLine("ProcessExited timeout (10s) has been passed; killing server.");
                // This kills the server right? Returning?
            }
        }

        /// <inheritdoc />
        public void MainLoop()
        {
            if (_mainLoop == null)
            {
                _mainLoop = new GameLoop(_time)
                {
                    SleepMode = SleepMode.Delay,
                    DetectSoftLock = true
                };
            }

            _uptimeStopwatch.Start();

            _mainLoop.Tick += (sender, args) => Update(args);

            _mainLoop.Update += (sender, args) =>
            {
                ServerUpTime.Set(_uptimeStopwatch.Elapsed.TotalSeconds);
            };

            // set GameLoop.Running to false to return from this function.
            _mainLoop.Run();

            _time.InSimulation = true;
            Cleanup();

            _shutdownEvent.Set();
        }

        public bool DisableLoadContext { private get; set; }

        public void OverrideMainLoop(IGameLoop gameLoop)
        {
            _mainLoop = gameLoop;
        }

        /// <summary>
        ///     Updates the console window title with performance statistics.
        /// </summary>
        private void UpdateTitle()
        {
            if (!Environment.UserInteractive || System.Console.IsInputRedirected)
            {
                return;
            }

            // every 1 second update stats in the console window title
            if ((_time.RealTime - _lastTitleUpdate).TotalSeconds < 1.0)
                return;

            var netStats = UpdateBps();
            System.Console.Title = string.Format("FPS: {0:N2} SD: {1:N2}ms | Net: ({2}) | Memory: {3:N0} KiB",
                Math.Round(_time.FramesPerSecondAvg, 2),
                _time.RealFrameTimeStdDev.TotalMilliseconds,
                netStats,
                Process.GetCurrentProcess().PrivateMemorySize64 >> 10);
            _lastTitleUpdate = _time.RealTime;
        }

        /// <summary>
        ///     Loads the server settings from the ConfigurationManager.
        /// </summary>
        private void LoadSettings()
        {
            var cfgMgr = IoCManager.Resolve<IConfigurationManager>();

            cfgMgr.RegisterCVar("net.tickrate", 60, CVar.ARCHIVE | CVar.REPLICATED | CVar.SERVER, i =>
            {
                var b = (byte) i;
                _time.TickRate = b;
                SendTickRateUpdateToClients(b);
            });

            cfgMgr.RegisterCVar("game.hostname", "MyServer", CVar.ARCHIVE);
            cfgMgr.RegisterCVar("game.maxplayers", 32, CVar.ARCHIVE);
            cfgMgr.RegisterCVar("game.type", GameType.Game);

            _time.TickRate = (byte) _config.GetCVar<int>("net.tickrate");

            Logger.InfoS("srv", $"Name: {ServerName}");
            Logger.InfoS("srv", $"TickRate: {_time.TickRate}({_time.TickPeriod.TotalMilliseconds:0.00}ms)");
            Logger.InfoS("srv", $"Max players: {MaxPlayers}");
        }

        private void SendTickRateUpdateToClients(byte newTickRate)
        {
            var msg = _network.CreateNetMessage<MsgSetTickRate>();
            msg.NewTickRate = newTickRate;

            _network.ServerSendToAll(msg);
        }

        // called right before main loop returns, do all saving/cleanup in here
        private void Cleanup()
        {
            // shut down networking, kicking all players.
            _network.Shutdown($"Server shutting down: {_shutdownReason}");

            // shutdown entities
            _entities.Shutdown();

            // Wrtie down exception log
            var logPath = _config.GetCVar<string>("log.path");
            var relPath = PathHelpers.ExecutableRelativeFile(logPath);
            Directory.CreateDirectory(relPath);
            var pathToWrite = Path.Combine(relPath,
                "Runtime-" + DateTime.Now.ToString("yyyy-MM-dd-THH-mm-ss") + ".txt");
            File.WriteAllText(pathToWrite, runtimeLog.Display(), EncodingHelpers.UTF8);

            AppDomain.CurrentDomain.ProcessExit -= ProcessExiting;

            //TODO: This should prob shutdown all managers in a loop.
        }

        private string UpdateBps()
        {
            var stats = IoCManager.Resolve<IServerNetManager>().Statistics;

            var bps =
                $"Send: {(stats.SentBytes - _lastSentBytes) >> 10:N0} KiB/s, Recv: {(stats.ReceivedBytes - _lastReceivedBytes) >> 10:N0} KiB/s";

            _lastSentBytes = stats.SentBytes;
            _lastReceivedBytes = stats.ReceivedBytes;

            return bps;
        }

        private void Update(FrameEventArgs frameEventArgs)
        {
            ServerCurTick.Set(_time.CurTick.Value);
            ServerCurTime.Set(_time.CurTime.TotalSeconds);

            UpdateTitle();
            _systemConsole.Update();

            _network.ProcessPackets();

            _modLoader.BroadcastUpdate(ModUpdateLevel.PreEngine, frameEventArgs);

            timerManager.UpdateTimers(frameEventArgs);
            _taskManager.ProcessPendingTasks();

            _components.CullRemovedComponents();
            _entities.Update(frameEventArgs.DeltaSeconds);

            _modLoader.BroadcastUpdate(ModUpdateLevel.PostEngine, frameEventArgs);

            _stateManager.SendGameStateUpdate();

            _watchdogApi.Heartbeat();
        }
    }

    /// <summary>
    ///     Type of game currently running.
    /// </summary>
    public enum GameType
    {
        MapEditor = 0,
        Game,
    }
}
