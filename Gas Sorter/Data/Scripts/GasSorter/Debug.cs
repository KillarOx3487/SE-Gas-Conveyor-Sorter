using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using System;
using System.Collections.Generic;
using System.Text;
using GasSorter.Shared.Backend;
using GasSorter.Shared;
using GasSorter.Modules;

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

    // CSV lines for full scan (log)
    private static readonly List<string> _allLines = new List<string>(256);

    // CSV lines for this scan
    private static readonly List<string> _lines = new List<string>(128);

    // Safety limits so chat doesn't explode
    private const int MaxLinesPerFlush = 200;
    private const int LinesPerChatMessage = 10;

        // Raw log format is code-only (no chat command). Switch here if desired.
        private enum RawLogFormat { Jsonl = 1, KeyValue = 2 }
        private const RawLogFormat RawLogFormatMode = RawLogFormat.Jsonl;


    // ---- rolling log (written only when DebugLogEnabled is true) ----
    private const string RollingFileName = "GasSorterDebug_Rolling.jsonl";
    private const int RollingMaxLines = 5000;
    private static bool _rollingInit = false;
    private static readonly List<string> _rolling = new List<string>(RollingMaxLines + 32);

    /// <summary>Call once at the start of RunGasControlScan when debug should run.</summary>
    public static void BeginScan(int logicTick)
    {
      _scanActive = true;
      _scanTick = logicTick;
      _allLines.Clear();
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

      // Rolling CSV output (only when explicitly enabled)
      if (GasSorterSession.DebugLogEnabled)
      {
        AppendScanToRolling();
        WriteRollingRaw();
      }

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
        }
            WriteRollingRaw();
    }

    private static void AppendScanToRolling()
    {
      if (!_rollingInit)
      {
        _rollingInit = true;
        _rolling.Clear();
        _rolling.Add("tick,sorter,filter,fwd,back");
      }

      // Append raw lines captured this scan
            for (int i = 0; i < _allLines.Count; i++)
                _rolling.Add(_allLines[i]);

      // Trim to last RollingMaxLines (keep header)
      if (_rolling.Count > RollingMaxLines) {
                int remove = _rolling.Count - RollingMaxLines;
                _rolling.RemoveRange(0, remove);
            }
    }

    private static void WriteRollingRaw()
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

            // Raw log line (JSONL or key=value)
            _allLines.Add(FormatRawLine(ref ctx, sorterName, fwd, back));
    }

    
        private static string FormatRawLine(ref GasSorterModuleContext ctx, string sorterNameEscapedForCsvChat, string fwdDesc, string backDesc)
        {
            // sorterNameEscapedForCsvChat uses doubled single quotes for CSV chat; convert back
            string sorterName = sorterNameEscapedForCsvChat?.Replace("''", "'");

            var sorter = ctx.Sorter;
            long sorterId = sorter != null ? sorter.EntityId : 0;

            long gridId = 0;
            string gridName = null;
            if (sorter != null && sorter.CubeGrid != null)
            {
                gridId = sorter.CubeGrid.EntityId;
                gridName = sorter.CubeGrid.CustomName;
            }

            // Forward block
            long fwdId = 0;
            string fwdType = null;
            string fwdDef = null;
            if (ctx.ForwardSlim != null && ctx.ForwardSlim.FatBlock != null)
            {
                var fb = ctx.ForwardSlim.FatBlock;
                fwdId = fb.EntityId;
                fwdType = fb.GetType().FullName;
                fwdDef = fb.BlockDefinition.ToString();
            }

            // Backward block
            long backId = 0;
            string backType = null;
            string backDef = null;
            if (ctx.BackwardSlim != null && ctx.BackwardSlim.FatBlock != null)
            {
                var bb = ctx.BackwardSlim.FatBlock;
                backId = bb.EntityId;
                backType = bb.GetType().FullName;
                backDef = bb.BlockDefinition.ToString();
            }

            if (RawLogFormatMode == RawLogFormat.KeyValue)
            {
                // Code-only alternate format (former "3"): key=value pairs
                var sb = new StringBuilder(256);
                sb.Append("tick=").Append(ctx.LogicTick);
                sb.Append("|sorterId=").Append(sorterId);
                sb.Append("|sorterName=").Append(sorterName ?? "");
                sb.Append("|gridId=").Append(gridId);
                sb.Append("|gridName=").Append(gridName ?? "");
                sb.Append("|filterMode=").Append((int)ctx.FilterMode);
                sb.Append("|filterName=").Append(ctx.FilterMode.ToString());
                sb.Append("|fwdDesc=").Append(fwdDesc ?? "");
                sb.Append("|fwdId=").Append(fwdId);
                sb.Append("|fwdType=").Append(fwdType ?? "");
                sb.Append("|fwdDef=").Append(fwdDef ?? "");
                sb.Append("|backDesc=").Append(backDesc ?? "");
                sb.Append("|backId=").Append(backId);
                sb.Append("|backType=").Append(backType ?? "");
                sb.Append("|backDef=").Append(backDef ?? "");
                return sb.ToString();
            }
            else
            {
                // JSONL (one JSON object per line)
                var sb = new StringBuilder(384);
                sb.Append('{');
                AppendJson(sb, "tick", ctx.LogicTick); sb.Append(',');
                AppendJson(sb, "sorterId", sorterId); sb.Append(',');
                AppendJson(sb, "sorterName", sorterName); sb.Append(',');
                AppendJson(sb, "gridId", gridId); sb.Append(',');
                AppendJson(sb, "gridName", gridName); sb.Append(',');
                AppendJson(sb, "filterMode", (int)ctx.FilterMode); sb.Append(',');
                AppendJson(sb, "filterName", ctx.FilterMode.ToString()); sb.Append(',');
                AppendJson(sb, "fwdDesc", fwdDesc); sb.Append(',');
                AppendJson(sb, "fwdId", fwdId); sb.Append(',');
                AppendJson(sb, "fwdType", fwdType); sb.Append(',');
                AppendJson(sb, "fwdDef", fwdDef); sb.Append(',');
                AppendJson(sb, "backDesc", backDesc); sb.Append(',');
                AppendJson(sb, "backId", backId); sb.Append(',');
                AppendJson(sb, "backType", backType); sb.Append(',');
                AppendJson(sb, "backDef", backDef);
                sb.Append('}');
                return sb.ToString();
            }
        }

        private static void AppendJson(StringBuilder sb, string key, int value)
        {
            sb.Append('"'); EscapeJsonInto(sb, key); sb.Append("\":").Append(value);
        }

        private static void AppendJson(StringBuilder sb, string key, long value)
        {
            sb.Append('"'); EscapeJsonInto(sb, key); sb.Append("\":").Append(value);
        }

        private static void AppendJson(StringBuilder sb, string key, string value)
        {
            sb.Append('"'); EscapeJsonInto(sb, key); sb.Append("\":");
            if (value == null) { sb.Append("null"); return; }
            sb.Append('"'); EscapeJsonInto(sb, value); sb.Append('"');
        }

        private static void EscapeJsonInto(StringBuilder sb, string s)
        {
            if (s == null) return;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append(' ');
                        else sb.Append(c);
                        break;
                }
            }
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
