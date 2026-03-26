using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Gigasoft.ProEssentials;
using Gigasoft.ProEssentials.Enums;

namespace RealTimeCircularBuffer
{
    /// <summary>
    /// ProEssentials WPF Real-Time Circular Buffer — 4 x 2 Million Points
    /// Local Memory / UseDataAtLocation + Custom Zoom Mode Menu
    ///
    /// Demonstrates the highest-performance real-time line charting pattern in
    /// ProEssentials v10 using PesgoWpf — the scientific graph object for
    /// continuous numeric X-axis data.
    ///
    /// KEY ARCHITECTURE — UseDataAtLocation (example 146 pattern):
    ///   The chart holds a pointer to app-owned arrays rather than its own copy.
    ///   PeData.Y.UseDataAtLocation(_yData, 8000000) tells the GPU staging
    ///   buffers to read directly from _yData — zero copy on every tick.
    ///   Compare to example 145 (FastCopyFrom) which copies 8M floats into the
    ///   chart's internal circular buffer each init — 146 is faster because no
    ///   copy ever happens between app memory and chart memory.
    ///
    /// CIRCULAR BUFFERS (v10):
    ///   PeData.CircularBuffers = true — AppendData writes into a ring buffer
    ///   rather than shifting all existing data left. With 2M points per subset
    ///   and 150 new samples per tick, shifting would move 8M floats every 15ms.
    ///   CircularBuffers eliminates that entirely. ComputeShader rendering reads
    ///   the ring in-order so there is no rendering penalty.
    ///
    /// GPU PIPELINE:
    ///   Direct3D + ComputeShader + StagingBufferX/Y + Filter2D3D
    ///   - ComputeShader constructs chart geometry on the GPU
    ///   - StagingBuffers are the DX transfer path from CPU memory to GPU
    ///   - Filter2D3D applies lossless pixel-level data reduction before the
    ///     GPU sees the data — at 2M points per subset only ~1000 are visible
    ///     at any zoom level; Filter2D3D avoids GPU work on invisible points
    ///   - AutoImageReset = false suppresses intermediate redraws during tick
    ///
    /// ZOOM MODE (custom right-click menu — from example 127 pattern):
    ///   Two behaviors controlled by right-click → Zoom Mode:
    ///
    ///   Stationary (default — example 146 behavior):
    ///     When zoomed in, the zoom window stays fixed. New data streams past
    ///     behind it. You are watching a frozen slice of time while the data
    ///     buffer continuously updates. Zoom in on a feature and study it while
    ///     the stream keeps running.
    ///
    ///   Scrolling (example 145 behavior):
    ///     When zoomed in, the zoom window advances with each tick to always
    ///     show the latest samples. The view "follows" the data stream. Useful
    ///     for monitoring the current state of the signal.
    ///
    ///   The only code difference is whether the timer tick advances
    ///   Zoom.MaxX / Zoom.MinX by nNewSamplesPerSubsetsPerTick each frame.
    ///
    /// Data: 4 subsets x 2,000,000 points = 8,000,000 floats of Y data.
    ///   Sin wave per subset, amplitude scales with subset index.
    ///   150 new samples per subset appended every 15ms = 600 Hz effective rate.
    ///
    /// Controls:
    ///   Mouse wheel           — horizontal zoom in / out
    ///   Right-click           — context menu
    ///   Right-click → Zoom Mode → Stationary | Scrolling
    /// </summary>
    public partial class MainWindow : Window
    {
        // ── App-owned data arrays (UseDataAtLocation — never copied to chart) ─
        private readonly float[] _xData = new float[2_000_000];
        private readonly float[] _yData = new float[8_000_000];

        // ── Per-tick append buffers ────────────────────────────────────────────
        // Layout: [s0p0 s0p1 … s0p149 | s1p0 … s1p149 | s2 … | s3 …]
        private readonly float[] _newY = new float[4 * 150];
        private readonly float[] _newX = new float[150];

        // ── Counters ──────────────────────────────────────────────────────────
        private long _rtCounter  = 2_000_000;  // tracks current X position
        private long _sinCounter = 2_000_000;  // drives sin wave phase

        // ── Zoom mode flag ────────────────────────────────────────────────────
        // false = Stationary (146 default) — zoom window does not follow data
        // true  = Scrolling  (145 behavior) — zoom window advances each tick
        private bool _zoomScrolls = false;

        // ── Custom menu indices ───────────────────────────────────────────────
        private const int MENU_SEP       = 0;
        private const int MENU_ZOOMMODE  = 1;
        private const int SUB_STATIONARY = 1;
        private const int SUB_SCROLLING  = 2;

        // ── Timer ─────────────────────────────────────────────────────────────
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly Random _rand = new Random();

        public MainWindow()
        {
            InitializeComponent();
        }

        // -----------------------------------------------------------------------
        // Pesgo1_Loaded — chart initialization
        // -----------------------------------------------------------------------
        void Pesgo1_Loaded(object sender, RoutedEventArgs e)
        {
            // Wire events before ReinitializeResetImage
            Pesgo1.PeCustomMenu += Pesgo1_PeCustomMenu;

            // =======================================================================
            // Step 1 — Data dimensions and CircularBuffers
            //
            // CircularBuffers = true must be set before UseDataAtLocation.
            // DuplicateDataX = PointIncrement: one shared X array for all 4 subsets —
            // avoids storing 4 x 2M = 8M X values. The chart uses X[0..N] for every
            // subset automatically.
            // =======================================================================
            Pesgo1.PeData.Subsets        = 4;
            Pesgo1.PeData.Points         = 2_000_000;
            Pesgo1.PeData.CircularBuffers = true;
            Pesgo1.PeData.DuplicateDataX  = DuplicateData.PointIncrement;

            // =======================================================================
            // Step 2 — Populate app-owned arrays
            //
            // X data: sequential 1..2000000.
            // Y data: sin wave per subset, amplitude = (s+1)*30, plus small noise.
            //   Pattern repeats every 8400 samples then Array.Copy tiles it across
            //   the full 2M point range for each subset.
            //   Subset layout in _yData (interleaved by subset, not by point):
            //     [0..1999999]         = subset 0
            //     [2000000..3999999]   = subset 1
            //     [4000000..5999999]   = subset 2
            //     [6000000..7999999]   = subset 3
            // =======================================================================
            for (int p = 0; p < 2_000_000; p++)
                _xData[p] = p + 1;

            var sub0 = new float[8400];
            var sub1 = new float[8400];
            var sub2 = new float[8400];
            var sub3 = new float[8400];

            for (int p = 0; p < 8400; p++)
            {
                sub0[p] = (float)(50 + (Math.Sin(p * 0.00075) * 1 * 30) + (_rand.NextDouble() * 15));
                sub1[p] = (float)(50 + (Math.Sin(p * 0.00075) * 2 * 30) + (_rand.NextDouble() * 15));
                sub2[p] = (float)(50 + (Math.Sin(p * 0.00075) * 3 * 30) + (_rand.NextDouble() * 15));
                sub3[p] = (float)(50 + (Math.Sin(p * 0.00075) * 4 * 30) + (_rand.NextDouble() * 15));
            }

            for (int j = 0; j < 1_991_600; j += 8400)
            {
                Array.Copy(sub0, 0, _yData, j,             8400);
                Array.Copy(sub1, 0, _yData, j + 2_000_000, 8400);
                Array.Copy(sub2, 0, _yData, j + 4_000_000, 8400);
                Array.Copy(sub3, 0, _yData, j + 6_000_000, 8400);
            }

            // =======================================================================
            // Step 3 — UseDataAtLocation
            //
            // Gives the chart a direct pointer to _xData and _yData.
            // No data is copied into chart-internal arrays — ever.
            // The GPU staging buffers read from these app arrays directly.
            // This is faster than FastCopyFrom (example 145) because the memory
            // transfer from app to chart is eliminated entirely.
            //
            // The chart reads _yData as a flat interleaved block:
            //   first 2M floats = subset 0, next 2M = subset 1, etc.
            // UseDataAtLocation must be called AFTER CircularBuffers = true.
            // =======================================================================
            Pesgo1.PeData.X.UseDataAtLocation(_xData, 2_000_000);
            Pesgo1.PeData.Y.UseDataAtLocation(_yData, 8_000_000);

            // =======================================================================
            // Step 4 — Manual axis scaling
            //
            // Manual scaling is essential for real-time performance with large data.
            // Without it the chart scans all 8M floats every tick to find min/max.
            // With manual scaling that scan is skipped entirely.
            // =======================================================================
            Pesgo1.PeGrid.Configure.ManualScaleControlY = ManualScaleControl.MinMax;
            Pesgo1.PeGrid.Configure.ManualMinY          = -110;
            Pesgo1.PeGrid.Configure.ManualMaxY          =  220;

            Pesgo1.PeGrid.Configure.ManualScaleControlX = ManualScaleControl.MinMax;
            Pesgo1.PeGrid.Configure.ManualMinX          =  0;
            Pesgo1.PeGrid.Configure.ManualMaxX          =  2_000_000;

            // =======================================================================
            // Step 5 — Appearance
            // =======================================================================
            Pesgo1.PeColor.BitmapGradientMode = false;
            Pesgo1.PeColor.QuickStyle         = QuickStyle.DarkNoBorder;

            Pesgo1.PeColor.SubsetColors[0] = Color.FromArgb(255, 255, 0,   0);   // red
            Pesgo1.PeColor.SubsetColors[1] = Color.FromArgb(255, 0,   255, 0);   // green
            Pesgo1.PeColor.SubsetColors[2] = Color.FromArgb(255, 255, 255, 0);   // yellow
            Pesgo1.PeColor.SubsetColors[3] = Color.FromArgb(255, 0,   255, 255); // cyan

            Pesgo1.PeConfigure.TextShadows  = TextShadows.BoldText;
            Pesgo1.PeFont.MainTitle.Bold    = true;
            Pesgo1.PeFont.SubTitle.Bold     = true;
            Pesgo1.PeFont.Label.Bold        = true;
            Pesgo1.PeFont.FontSize          = Gigasoft.ProEssentials.Enums.FontSize.Small;
            Pesgo1.PeFont.SizeGlobalCntl    = 1.4F;
            Pesgo1.PeFont.Fixed             = true;

            Pesgo1.PePlot.Option.LineShadows  = true;
            Pesgo1.PePlot.SubsetLineTypes[0]  = LineType.ThinSolid;
            Pesgo1.PePlot.SubsetLineTypes[1]  = LineType.ThinSolid;
            Pesgo1.PePlot.SubsetLineTypes[2]  = LineType.ThinSolid;
            Pesgo1.PePlot.SubsetLineTypes[3]  = LineType.ThinSolid;

            Pesgo1.PePlot.Method      = SGraphPlottingMethod.Line;
            Pesgo1.PePlot.DataShadows = DataShadows.None;

            Pesgo1.PeLegend.SimpleLine = true;

            // =======================================================================
            // Step 6 — Titles
            // =======================================================================
            Pesgo1.PeString.MainTitle = "4 x 2 Million Real-Time Circular Buffer — Local Memory";
            Pesgo1.PeString.SubTitle  = "Right-click \u2192 Zoom Mode to toggle Stationary vs Scrolling zoom";

            // =======================================================================
            // Step 7 — Layout
            // =======================================================================
            Pesgo1.PeGrid.Configure.AutoMinMaxPaddingX = 0;
            Pesgo1.PeConfigure.ImageAdjustLeft         = 50;
            Pesgo1.PeConfigure.ImageAdjustRight        = 0;

            // =======================================================================
            // Step 8 — Dialog and menu restrictions
            // =======================================================================
            Pesgo1.PeUserInterface.Dialog.PlotCustomization = false;
            Pesgo1.PeUserInterface.Dialog.Page2             = false;
            Pesgo1.PeUserInterface.Allow.Popup              = true;
            Pesgo1.PeUserInterface.Dialog.Axis              = false;
            Pesgo1.PeUserInterface.Dialog.Subsets           = true;
            Pesgo1.PeUserInterface.Allow.TextExport         = false;
            Pesgo1.PeUserInterface.Dialog.AllowEmfExport    = false;
            Pesgo1.PeUserInterface.Dialog.AllowWmfExport    = false;
            Pesgo1.PeUserInterface.Dialog.RandomPointsToExport = true;
            Pesgo1.PeUserInterface.Allow.FocalRect          = false;

            Pesgo1.PeUserInterface.Menu.MarkDataPoints = MenuControl.Hide;
            Pesgo1.PeUserInterface.Menu.DataShadow     = MenuControl.Hide;

            // =======================================================================
            // Step 9 — Custom Zoom Mode menu (example 127 pattern)
            //
            // Two items appended to the bottom of the built-in right-click menu:
            //
            //   [0]  "|"                     — separator line
            //   [1]  "Zoom Mode|Stationary|Scrolling"
            //                                — submenu with 2 radio-style options
            //
            // CustomMenuText format for a submenu: "Title|Item1|Item2|..."
            // CustomMenuState[menuIndex, subIndex]:
            //   subIndex 0 = the top-level popup item (always unchecked)
            //   subIndex 1 = first sub-item, subIndex 2 = second sub-item, etc.
            //
            // Default: Stationary checked (matches 146 behavior).
            // =======================================================================
            Pesgo1.PeUserInterface.Menu.CustomMenuText[MENU_SEP]      = "|";
            Pesgo1.PeUserInterface.Menu.CustomMenuText[MENU_ZOOMMODE] = "Zoom Mode|Stationary|Scrolling";

            Pesgo1.PeUserInterface.Menu.CustomMenuState[MENU_ZOOMMODE, SUB_STATIONARY] = CustomMenuState.Checked;
            Pesgo1.PeUserInterface.Menu.CustomMenuState[MENU_ZOOMMODE, SUB_SCROLLING]  = CustomMenuState.UnChecked;

            Pesgo1.PeUserInterface.Menu.CustomMenuLocation[MENU_SEP]      = CustomMenuLocation.Bottom;
            Pesgo1.PeUserInterface.Menu.CustomMenuLocation[MENU_ZOOMMODE] = CustomMenuLocation.Bottom;

            // =======================================================================
            // Step 10 — Zoom
            // =======================================================================
            Pesgo1.PeUserInterface.Scrollbar.ScrollingHorzZoom         = true;
            Pesgo1.PeUserInterface.Scrollbar.MouseWheelFunction        = MouseWheelFunction.HorizontalZoom;

            // =======================================================================
            // Step 11 — Anti-aliasing
            // =======================================================================
            Pesgo1.PeConfigure.AntiAliasText     = true;
            Pesgo1.PeConfigure.AntiAliasGraphics = true;

            // =======================================================================
            // Step 12 — GPU rendering pipeline
            //
            // RenderEngine.Direct3D   — GPU rendering, required for ComputeShader
            // ComputeShader           — builds chart geometry on the GPU
            // StagingBufferX/Y        — DX transfer path: CPU app memory → GPU
            // Filter2D3D              — lossless pixel-level data reduction:
            //                           at 2M points only ~1000 are visible per
            //                           pixel-width; Filter2D3D skips invisible
            //                           points before GPU transfer
            // AutoImageReset = false  — suppresses intermediate redraws during tick,
            //                           prevents tearing on fast update rates
            // =======================================================================
            Pesgo1.PeConfigure.PrepareImages              = true;
            Pesgo1.PeConfigure.CacheBmp                   = true;
            Pesgo1.PeConfigure.RenderEngine               = RenderEngine.Direct3D;
            Pesgo1.PeData.ComputeShader                   = true;
            Pesgo1.PeData.StagingBufferX                  = true;
            Pesgo1.PeData.StagingBufferY                  = true;
            Pesgo1.PeData.Filter2D3D                      = true;
            Pesgo1.PeSpecial.AutoImageReset               = false;
            Pesgo1.PeUserInterface.Cursor.HourGlassThreshold = 99_999_999;

            Pesgo1.PeFunction.Force3dxVerticeRebuild = true;
            Pesgo1.PeFunction.ReinitializeResetImage();

            // =======================================================================
            // Step 13 — Start timer (15ms ≈ 67 fps, 600 new samples/subset/sec)
            // =======================================================================
            _timer.Interval = TimeSpan.FromMilliseconds(15);
            _timer.Tick    += Timer_Tick;
            _timer.Start();
        }

        // -----------------------------------------------------------------------
        // Timer_Tick — appends 150 new samples per subset every 15ms
        //
        // ZOOM MODE BEHAVIOR:
        //
        //   Stationary (_zoomScrolls = false — default, example 146):
        //     Zoom.MaxX / Zoom.MinX are NOT advanced. The zoom window stays
        //     fixed in time. New data streams past behind it. Zoom in on a
        //     feature and study it while the live stream keeps running.
        //
        //   Scrolling (_zoomScrolls = true — example 145 behavior):
        //     When Zoom.Mode is true (user has zoomed in), Zoom.MaxX and
        //     Zoom.MinX are each advanced by nNew every tick. The window
        //     "follows" the latest data, always showing the most recent samples.
        //
        // The only code difference between the two modes is the if-block
        // that conditionally shifts the zoom window. Everything else is identical.
        // -----------------------------------------------------------------------
        private void Timer_Tick(object sender, EventArgs e)
        {
            const int nNew = 150; // new samples per subset per tick

            // Generate new Y data (interleaved: s0p0..s0p149 | s1p0..s1p149 | ...)
            for (int s = 0; s < 4; s++)
            {
                for (int p = 0; p < nNew; p++)
                {
                    _newY[(s * nNew) + p] = (float)(
                        50 +
                        (Math.Sin((_sinCounter + p) * 0.00075) * (s + 1) * 30) +
                        (_rand.NextDouble() * 15));
                }
            }

            // Generate new X data (same values used for all subsets via DuplicateDataX)
            for (int p = 0; p < nNew; p++)
                _newX[p] = _rtCounter + p + 1;

            // Append — CircularBuffers means these wrap around in the ring,
            // no data shift, no reallocation
            Pesgo1.PeData.Y.AppendData(_newY, nNew);
            Pesgo1.PeData.X.AppendData(_newX, nNew);

            _rtCounter  += nNew;
            _sinCounter += nNew;

            // Advance the manual axis extents to keep the full 2M window current
            Pesgo1.PeGrid.Configure.ManualMinX = _rtCounter - 2_000_000;
            Pesgo1.PeGrid.Configure.ManualMaxX = _rtCounter;

            // ── Zoom mode: advance window only when Scrolling is selected ──────
            if (_zoomScrolls && Pesgo1.PeGrid.Zoom.Mode)
            {
                Pesgo1.PeGrid.Zoom.MaxX += nNew;
                Pesgo1.PeGrid.Zoom.MinX += nNew;
            }

            if (_sinCounter >= (long)1e15) _sinCounter = 0;

            Pesgo1.PeFunction.Force3dxVerticeRebuild = true;
            Pesgo1.PeFunction.ReinitializeResetImage();
            Pesgo1.Invalidate();
        }

        // -----------------------------------------------------------------------
        // Pesgo1_PeCustomMenu — handles Zoom Mode submenu
        //
        // MenuIndex 1 = Zoom Mode popup
        //   SubmenuIndex 1 = Stationary
        //   SubmenuIndex 2 = Scrolling
        //
        // Radio-style: uncheck all sub-items, then check the selected one.
        // Set _zoomScrolls to match the selection. No reinit needed — the flag
        // takes effect on the next timer tick.
        // -----------------------------------------------------------------------
        private void Pesgo1_PeCustomMenu(object sender,
            Gigasoft.ProEssentials.EventArg.CustomMenuEventArgs e)
        {
            if (e.MenuIndex != MENU_ZOOMMODE) return;

            // Uncheck both options, then check whichever was picked
            Pesgo1.PeUserInterface.Menu.CustomMenuState[MENU_ZOOMMODE, SUB_STATIONARY] = CustomMenuState.UnChecked;
            Pesgo1.PeUserInterface.Menu.CustomMenuState[MENU_ZOOMMODE, SUB_SCROLLING]  = CustomMenuState.UnChecked;
            Pesgo1.PeUserInterface.Menu.CustomMenuState[MENU_ZOOMMODE, e.SubmenuIndex] = CustomMenuState.Checked;

            _zoomScrolls = (e.SubmenuIndex == SUB_SCROLLING);

            // Update subtitle to reflect current mode
            Pesgo1.PeString.SubTitle = _zoomScrolls
                ? "Zoom Mode: Scrolling — zoom window follows latest data"
                : "Zoom Mode: Stationary — zoom window stays fixed, data streams past";
        }

        // -----------------------------------------------------------------------
        // Window_Closing — stop timer cleanly
        // -----------------------------------------------------------------------
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _timer.Stop();
        }
    }
}
