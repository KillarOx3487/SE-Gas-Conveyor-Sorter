using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRage.Game;
using System;
using System.Collections.Generic;
using System.Text;

namespace GasSorter
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class GasSorterSession : MySessionComponentBase
    {
        // Unique key inside CustomData so we don't stomp other mods/users
        private const string CustomDataKey = "[GasSorter]GasControl=";

        private bool _initialized = false;
        private bool _controlsCreated = false;

        // Track which blocks we've hooked for custom info so we don't subscribe multiple times
        private readonly HashSet<long> _infoHooked = new HashSet<long>();

        // Tick counter for throttling gas logic
        private int _logicTick = 0;

        public override void UpdateBeforeSimulation()
        {
            // Run initialization once when the game systems are ready
            if (!_initialized)
            {
                if (MyAPIGateway.TerminalControls == null || MyAPIGateway.Session == null)
                    return;

                _initialized = true;

                // Debug: show a message in chat so we know this component actually ran
                MyAPIGateway.Utilities.ShowMessage("GasSorter", "GasSorterSession initialized");

                MyAPIGateway.TerminalControls.CustomControlGetter += TerminalControlGetter;
            }

            if (MyAPIGateway.Session == null)
                return;

            // --- Call gas logic skeleton every ~30 ticks ---
            _logicTick++;
            if (_logicTick % 30 == 0) // roughly every 0.5s at 60 FPS
            {
                GasSorterGasLogic.RunGasControlScan(_logicTick);
            }
        }

        protected override void UnloadData()
        {
            if (MyAPIGateway.TerminalControls != null)
            {
                MyAPIGateway.TerminalControls.CustomControlGetter -= TerminalControlGetter;
            }

            _infoHooked.Clear();
        }

        private void TerminalControlGetter(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            // Only care about conveyor sorters
            var sorter = block as IMyConveyorSorter;
            if (sorter == null)
                return;

            // Create the checkbox once (per game session)
            if (!_controlsCreated)
            {
                CreateGasControlCheckbox();
                _controlsCreated = true;
            }

            // Hook custom info for this specific sorter instance (once per block)
            if (!_infoHooked.Contains(block.EntityId))
            {
                block.AppendingCustomInfo += Block_AppendingCustomInfo;
                _infoHooked.Add(block.EntityId);

                // Force info to regenerate after we attach the handler
                block.RefreshCustomInfo();
            }
        }

        private void CreateGasControlCheckbox()
        {
            var checkbox =
                MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyConveyorSorter>(
                    "GasControlEnabled");

            checkbox.Title = MyStringId.GetOrCompute("Gas Control");
            checkbox.Tooltip = MyStringId.GetOrCompute(
                "When enabled, this sorter controls gas flow.\n" +
                "When disabled, gas behaves like vanilla.");

            checkbox.Getter = block => GetGasControlEnabled(block);
            checkbox.Setter = (block, value) => SetGasControlEnabled(block, value);

            checkbox.SupportsMultipleBlocks = true;
            checkbox.Visible = block => true;
            checkbox.Enabled = block => true;

            MyAPIGateway.TerminalControls.AddControl<IMyConveyorSorter>(checkbox);
        }

        // --- CustomData helpers for persistence ---

        public static bool GetGasControlEnabled(IMyTerminalBlock block)
        {
            if (block == null)
                return false;

            if (string.IsNullOrEmpty(block.CustomData))
                return false;

            // Look for our key in CustomData
            var lines = block.CustomData.Split('\n');
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith(CustomDataKey, StringComparison.OrdinalIgnoreCase))
                {
                    var valuePart = line.Substring(CustomDataKey.Length).Trim();
                    return valuePart == "1" || valuePart.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }

            return false; // default OFF
        }

        public static void SetGasControlEnabled(IMyTerminalBlock block, bool enabled)
        {
            if (block == null)
                return;

            string cd = block.CustomData ?? string.Empty;
            var sb = new StringBuilder();

            bool found = false;
            var lines = cd.Split('\n');
            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r'); // keep original line breaks intact-ish
                if (line.TrimStart().StartsWith(CustomDataKey, StringComparison.OrdinalIgnoreCase))
                {
                    // Replace our line with the new value
                    sb.Append(CustomDataKey).Append(enabled ? "1" : "0").Append('\n');
                    found = true;
                }
                else if (line.Length > 0)
                {
                    sb.Append(line).Append('\n');
                }
            }

            if (!found)
            {
                // Append our setting if it wasn't present
                sb.Append(CustomDataKey).Append(enabled ? "1" : "0").Append('\n');
            }

            block.CustomData = sb.ToString();

            // Force info to refresh so player sees updated status
            block.RefreshCustomInfo();
        }

        // --- Custom info hook per block ---

        private void Block_AppendingCustomInfo(IMyTerminalBlock block, StringBuilder sb)
        {
            var sorter = block as IMyConveyorSorter;
            if (sorter == null)
                return;

            bool enabled = GetGasControlEnabled(sorter);

            sb.AppendLine("Gas Control:");
            sb.AppendLine(enabled ? "  Status: ENABLED" : "  Status: DISABLED");

            if (!enabled)
            {
                sb.AppendLine("  Mode: Vanilla gas flow");
            }
            else
            {
                sb.AppendLine("  Mode: Directional valve");
                // Later we’ll add:
                // sb.AppendLine("  Filter: Oxygen Only / Hydrogen Only / All Gases");
            }
        }
    }
}
