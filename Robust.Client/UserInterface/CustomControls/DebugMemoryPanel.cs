using System;
using System.Linq;
using System.Runtime;
using Robust.Client.Graphics.Drawing;
using Robust.Client.Interfaces;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Robust.Client.UserInterface.CustomControls
{
    internal sealed class DebugMemoryPanel : PanelContainer
    {

        private readonly GameController _gameController = default!;
        private readonly Label _label;

        private readonly long[] _allocDeltas = new long[60];
        private long _lastAllocated;
        private int _allocDeltaIndex;

        public DebugMemoryPanel()
        {
            _gameController = (GameController) IoCManager.Resolve<IGameController>();
            // Disable this panel outside .NET Core since it's useless there.
#if !NETCOREAPP
            Visible = false;
#endif

            SizeFlagsHorizontal = SizeFlags.None;

            AddChild(_label = new Label());

            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#7d41ff8a")
            };

            PanelOverride.SetContentMarginOverride(StyleBox.Margin.All, 4);

            MouseFilter = _label.MouseFilter = MouseFilterMode.Ignore;
        }

        protected override void FrameUpdate(FrameEventArgs args)
        {
            base.FrameUpdate(args);

            if (!VisibleInTree)
            {
                return;
            }

            _label.Text = GetMemoryInfo();
        }

        private string GetMemoryInfo()
        {
#if NETCOREAPP
            var allocated = GC.GetTotalMemory(false);
            LogAllocSize(allocated);
            var info = GC.GetGCMemoryInfo();
            return $@"{(GCSettings.IsServerGC ? "Server" : "Workstation")} GC, {GCSettings.LatencyMode}
Last Heap Size: {FormatBytes(info.HeapSizeBytes)}
Total Allocated: {FormatBytes(allocated)}
Collections: {GC.CollectionCount(0)} {GC.CollectionCount(1)} {GC.CollectionCount(2)}
Alloc Rate: {FormatBytes(CalculateAllocRate())} / frame
Fragmented: {FormatBytes(info.FragmentedBytes)}
Imp GC Events: {_gameController.ImposedGcEvents}
Imp GC Total: {_gameController.ImposedGcDelayTotal:s\.fffffff}s
Imp GC Cur Delay: {_gameController.ImposedGcDelayLatest:s\.fffffff}s
Imp GC Max Delay: {_gameController.ImposedGcDelayMax:s\.fffffff}s
Imp GC Min Delay: {_gameController.ImposedGcDelayMin:s\.fffffff}s
Imp GC Avg Delay: {_gameController.ImposedGcDelayAverage:s\.fffffff}s
Imp GC Avg Large Delay: {_gameController.ImposedGcLargeDelayAverage:s\.fffffff}s
";
#else
            return "Memory information needs .NET Core";
#endif
        }

#if NETCOREAPP
        private static string FormatBytes(long bytes)
        {
            return $"{bytes / 1024} KiB";
        }
#endif

        private void LogAllocSize(long allocated)
        {
            var delta = allocated - _lastAllocated;
            _lastAllocated = allocated;

            // delta is < 0 if the GC ran a collection so it dropped.
            // In that case, treat it as a dud by writing write -1.
            _allocDeltas[_allocDeltaIndex++ % _allocDeltas.Length] = Math.Max(-1, delta);
        }

        private long CalculateAllocRate()
        {
            return (long) _allocDeltas.Where(x => x >= 0).Average();
        }
    }
}
