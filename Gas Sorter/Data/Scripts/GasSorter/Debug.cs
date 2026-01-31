using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using System;
using System.Collections.Generic;
using System.Text;
using GasSorter.Shared.Backend;

namespace GasSorter
{
    /// <summary>
    /// Switchable debug output. No gameplay side effects.
    /// Enable/disable with: /gassorter debug on|off
    ///
    /// Behavior:
    /// - When active, collects per-sorter lines into a buffer during a scan tick.
    /// - At end of the scan, prints the whole buffer (chunked) and clears it.
    /// </summary>
    public sealed class GasSorterDebugModule : IGasSorterModule
    {
        public string Name => "Debug";
        public bool Enabled => GasSorterSession.DebugEnabled;

        // Only do debug collection/printing occasionally
        public int TickInterval => 300;

        // ---- scan-batch state (server-side) ----
        private static bool _scanActive = false;
        private static int _scanTick = -1;

        // CSV lines for this scan
        private static readonly List<string> _lines = new List<string>(128);

        // Safety limits so chat doesn't explode
        private const int MaxLinesPerFlush = 200;
        private const int LinesPerChatMessage = 10;

        // ---- rolling log (written only when DebugLogEnabled is true) ----
        private const string RollingFileName = "GasSorterDebug_Rolling.csv";
        private const int RollingMaxLines = 5000;
        private static bool _rollingInit = false;
        private static readonly List<string> _rolling = new List<string>(RollingMaxLines + 32);


        /// <summary>Call once at the start of RunGasControlScan when debug should run.</summary>
        public static void BeginScan(int logicTick)
        {
            _scanActive = true;
            _scanTick = logicTick;
            _lines.Clear();

            // Optional header (printed as first line)
            _lines.Add("tick,sorter,filter,fwd,back");
        }

        /// <summary>Call once at the end of RunGasControlScan.</summary>
        public static void EndScan()
        {
            if (!_scanActive)
                return;

            _scanActive = false;

            // Only print on server to avoid duplicates
            if (MyAPIGateway.Multiplayer != null && !MyAPIGateway.Multiplayer.IsServer)
                return;

            if (MyAPIGateway.Utilities == null)
                return;

            if (_lines.Count <= 1)
            {
                // header only => nothing captured
                MyAPIGateway.Utilities.ShowMessage(GSTags.ChatPrefixDbg, $"[{_scanTick}] (no active gas sorters)");
                return;
            }

            int total = _lines.Count - 1; // minus header
            int cappedTotal = total;

            if (total > MaxLinesPerFlush)
            {
                cappedTotal = MaxLinesPerFlush;
                // keep header + first MaxLinesPerFlush lines
                _lines.RemoveRange(1 + MaxLinesPerFlush, _lines.Count - (1 + MaxLinesPerFlush));
                _lines.Add($"[{_scanTick}],(truncated),lines={total},cap={MaxLinesPerFlush},,");
            }

            // Print in chunks to avoid chat truncation
            int idx = 0;
            while (idx < _lines.Count)
            {
                var sb = new StringBuilder(512);

                int take = Math.Min(LinesPerChatMessage, _lines.Count - idx);
                for (int i = 0; i < take; i++)
                {
                    sb.Append(_lines[idx + i]);
                    if (i != take - 1)
                        sb.Append(" | ");
                }

                MyAPIGateway.Utilities.ShowMessage(
                    GSTags.ChatPrefixDbg,
                    sb.ToString()
                );

                idx += take;
            }

            // Summary line
            MyAPIGateway.Utilities.ShowMessage(
                GSTags.ChatPrefixDbg,
                $"[{_scanTick}] sorters={cappedTotal}" + (total != cappedTotal ? $" (truncated from {total})" : "")
            );
        }

        
        /// <summary>
        /// Ensure the rolling CSV file exists immediately (creates header + writes current buffer).
        /// Safe to call from chat command handlers.
        /// </summary>
        public static void EnsureRollingLogFile()
        {
            if (MyAPIGateway.Utilities == null)
                return;

            if (!_rollingInit)
            {
                _rollingInit = true;
                _rolling.Clear();
                _rolling.Add("tick,sorter,filter,fwd,back");
            }

            WriteRollingCsv();
        }

        private static void AppendScanToRolling()
        {
            if (!_rollingInit)
            {
                _rollingInit = true;
                _rolling.Clear();
                _rolling.Add("tick,sorter,filter,fwd,back");
            }

            // Skip header at index 0 in _lines, append captured lines
            for (int i = 1; i < _lines.Count; i++)
                _rolling.Add(_lines[i]);

            // Trim to last RollingMaxLines (keep header)
            int maxTotal = RollingMaxLines + 1;
            if (_rolling.Count > maxTotal)
            {
                int remove = _rolling.Count - maxTotal;
                // never remove header
                _rolling.RemoveRange(1, remove);
            }
        }

        private static void WriteRollingCsv()
        {
            if (MyAPIGateway.Utilities == null)
                return;

            using (var writer = MyAPIGateway.Utilities.WriteFileInLocalStorage(RollingFileName, typeof(GasSorterSession)))
            {
                for (int i = 0; i < _rolling.Count; i++)
                    writer.WriteLine(_rolling[i]);
            }
        }

public void Apply(ref GasSorterModuleContext ctx)
        {
            if (!_scanActive)
                return;

            // Don't collect if chat isn't available
            if (MyAPIGateway.Utilities == null)
                return;

            // Build a CSV-ish line.
            // Example:
            // 300,'H2_2',Both,GasTank,GasTank
            string sorterName = ctx.Sorter?.CustomName;
            if (string.IsNullOrWhiteSpace(sorterName))
                sorterName = ctx.Sorter?.DefinitionDisplayNameText ?? "Sorter";

            // Quote sorter name (and escape embedded quotes)
            sorterName = sorterName.Replace("'", "''");

            string fwd = Describe(ctx.ForwardSlim);
            string back = Describe(ctx.BackwardSlim);

            _lines.Add($"{ctx.LogicTick},'{sorterName}',{ctx.FilterMode},{fwd},{back}");
        }

        private static string Describe(IMySlimBlock slim)
        {
            if (slim == null || slim.FatBlock == null) return "none";

            var fat = slim.FatBlock;

            if (fat is Sandbox.ModAPI.IMyGasTank) return "GasTank";

            string name = fat.DefinitionDisplayNameText;
            if (!string.IsNullOrWhiteSpace(name)) return name;

            return fat.GetType().Name;
        }
    }
}
