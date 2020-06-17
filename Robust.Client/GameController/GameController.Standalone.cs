using System;
using System.Threading;
using Robust.Client.Interfaces;
using Robust.Shared.Interfaces.Timing;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Timing;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Robust.Client
{

    internal partial class GameController
    {

        private IGameLoop _mainLoop = default!;

        [Dependency] private readonly IGameTiming _gameTiming = default!;

        private static bool _hasStarted;

        public static void Main(string[] args)
        {
            Start(args);
        }

        public static void Start(string[] args, bool contentStart = false)
        {
            if (_hasStarted)
            {
                throw new InvalidOperationException("Cannot start twice!");
            }

            _hasStarted = true;

            if (CommandLineArgs.TryParse(args, out var parsed))
            {
                ParsedMain(parsed, contentStart);
            }
        }

        private static void ParsedMain(CommandLineArgs args, bool contentStart)
        {
            IoCManager.InitThread();

            var mode = args.Headless ? DisplayMode.Headless : DisplayMode.Clyde;

            InitIoC(mode);

            var gc = (GameController) IoCManager.Resolve<IGameController>();
            gc.SetCommandLineArgs(args);

            // When the game is ran with the startup executable being content,
            // we have to disable the separate load context.
            // Otherwise the content assemblies will be loaded twice which causes *many* fun bugs.
            gc._disableAssemblyLoadContext = contentStart;
            if (!gc.Startup())
            {
                Logger.Fatal("Failed to start game controller!");
                return;
            }

            gc.MainLoop(mode);

            Logger.Debug("Goodbye");
            IoCManager.Clear();
        }

        public void OverrideMainLoop(IGameLoop gameLoop)
        {
            _mainLoop = gameLoop;
        }

        private int _renderNoGcRegionSize = 4 * 1024;

        private long _imposedGcDelayTicks = 0;
        private long _imposedGcDelayTicksMin = long.MaxValue;
        private long _imposedGcDelayTicksMax = 0;
        private long _imposedGcEvents = 0;

        private long _imposedGcLargeDelayTicks = 0;
        private long _imposedGcLargeEvents = 0;

        public TimeSpan ImposedGcDelayMin => new TimeSpan(_imposedGcDelayTicksMin);
        public TimeSpan ImposedGcDelayMax => new TimeSpan(_imposedGcDelayTicksMax);
        public TimeSpan ImposedGcDelayAverage => new TimeSpan( (long)Math.Round(_imposedGcDelayTicks / (double) _imposedGcEvents, MidpointRounding.AwayFromZero) );
        public TimeSpan ImposedGcLargeDelayAverage => new TimeSpan( (long)Math.Round(_imposedGcLargeDelayTicks / (double) _imposedGcLargeEvents, MidpointRounding.AwayFromZero) );

        public void ResetImposedGcDelayStats()
        {
            _imposedGcDelayTicks = 0;
            _imposedGcDelayTicksMin = long.MaxValue;
            _imposedGcDelayTicksMax = 0;
            _imposedGcEvents = 0;
        }

        public void MainLoop(DisplayMode mode)
        {
            if (_mainLoop == null)
            {
                _mainLoop = new GameLoop(_gameTiming)
                {
                    SleepMode = mode == DisplayMode.Headless ? SleepMode.Delay : SleepMode.None
                };
            }

            _mainLoop.Tick += (sender, args) =>
            {
                if (_mainLoop.Running)
                {
                    Update(args);
                }
            };

            _mainLoop.Render += (sender, args) =>
            {
                if (_mainLoop.Running)
                {
                    _gameTiming.CurFrame++;
                    try
                    {
                        GC.TryStartNoGCRegion(_renderNoGcRegionSize);
                        _clyde.Render();
                    }
                    finally
                    {
                        try
                        {
                            GC.EndNoGCRegion();
                        }
                        catch (InvalidOperationException ioe)
                        {
                            var prev = _renderNoGcRegionSize;
                            if (ioe.Message == "Allocated memory exceeds specified memory for NoGCRegion mode")
                            {
                                _renderNoGcRegionSize = Math.Min(_renderNoGcRegionSize + 4 * 1024, 2 * 1024 * 1024);
                            }

                            if (prev != _renderNoGcRegionSize)
                            {
                                if (_renderNoGcRegionSize < 2 * 1024 * 1024)
                                {
                                    _logManager.GetSawmill("nogc").Info($"Expanding No-GC Region to {_renderNoGcRegionSize / 1024}KB");
                                }
                                else
                                {
                                    _logManager.GetSawmill("nogc").Warning($"Expanding No-GC Region to {_renderNoGcRegionSize / 1024}KB and capped");
                                }
                            }
                        }
                    }

                    var start = Stopwatch.GetTimestamp();
                    GC.Collect(0, GCCollectionMode.Optimized, false, true);
                    var fin = Stopwatch.GetTimestamp();
                    var elapsed = fin - start;
                    _imposedGcDelayTicks += elapsed;
                    _imposedGcEvents += 1;
                    if (elapsed > _imposedGcDelayTicksMax)
                    {
                        _imposedGcDelayTicksMax = elapsed;
                    }

                    if (elapsed < _imposedGcDelayTicksMin)
                    {
                        _imposedGcDelayTicksMin = elapsed;
                    }

                    if (elapsed > _imposedGcDelayTicksMin * 10)
                    {
                        _imposedGcLargeDelayTicks += elapsed;
                        _imposedGcLargeEvents += 1;
                    }
                }
            };
            _mainLoop.Input += (sender, args) =>
            {
                if (_mainLoop.Running)
                {
                    _clyde.ProcessInput(args);
                }
            };

            _mainLoop.Update += (sender, args) =>
            {
                if (_mainLoop.Running)
                {
                    _frameProcessMain(args);
                }
            };

            // set GameLoop.Running to false to return from this function.
            _mainLoop.Run();
        }

    }

}
