using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using GasSorter.Shared.Backend;

namespace GasSorter
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class GasSorterSession : MySessionComponentBase
    {
        private int _infoRefreshTick = 0;

        // Unique key inside CustomData so we don't stomp other mods/users
        private const string CustomDataKey = GSTags.CustomDataKey_GasControl;

        private bool _logicInitialized = false;
        private bool _uiInitialized = false;
        private bool _controlsCreated = false;

        // Terminal UI ordering helpers (client-side)
        private static bool _reorderedForSorterControls = false;
        private static bool _dumpedSorterControls = false;

        // Track which blocks we've hooked for custom info so we don't subscribe multiple times
        private readonly HashSet<long> _infoHooked = new HashSet<long>();

        // Tick counter for throttling gas logic
        private int _logicTick = 0;

        // --- Debug Switch ---
        public static bool DebugEnabled { get; private set; } = false;

        public override void UpdateBeforeSimulation()
        {
            // Wait for session to exist on both client and server
            if (MyAPIGateway.Session == null)
                return;

            // -------------------------
            // SERVER: logic init + tick
            // -------------------------
            bool isServer = (MyAPIGateway.Multiplayer == null) || MyAPIGateway.Multiplayer.IsServer;
            if (isServer)
            {
                if (!_logicInitialized)
                {
                    _logicInitialized = true;
                    // Do NOT call ShowMessage here (dedicated server has no GUI/chat UI).
                    // Optional log:
                    // MyLog.Default.WriteLineAndConsole("[GasSorter] Server logic initialized");
                }

                _logicTick++;
                if (_logicTick % 30 == 0) // roughly every 0.5s at 60 FPS
                    GasSorterGasLogic.RunGasControlScan(_logicTick);
            }

            // -------------------------
            // CLIENT: UI init + refresh
            // -------------------------
            if (!_uiInitialized)
            {
                if (MyAPIGateway.Utilities == null)
                    return;

                if (MyAPIGateway.Utilities.IsDedicated)
                    return;

                if (MyAPIGateway.TerminalControls == null)
                    return;

                _uiInitialized = true;

                // Debug: show a message in chat so we know this component actually ran
                MyAPIGateway.Utilities.ShowMessage(GSTags.ChatPrefix, "GasSorterSession initialized");

                MyAPIGateway.Utilities.MessageEntered += OnMessageEntered;
                MyAPIGateway.TerminalControls.CustomControlGetter += TerminalControlGetter;
            }

            // UI refresh only makes sense on a client
            UpdateLiveInfoRefresh();
        }

        private void OnMessageEntered(string messageText, ref bool sendToOthers)
        {
            if (string.IsNullOrWhiteSpace(messageText))
                return;

            // Only handle our commands
            // Examples:
            //  /gassorter debug on
            //  /gassorter debug off
            //  /gassorter debug
            string msg = messageText.Trim();

            if (!msg.StartsWith(GSTags.ChatRoot, StringComparison.OrdinalIgnoreCase))
                return;

            // Do not broadcast our command to chat
            sendToOthers = false;

            var parts = msg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            // parts[0] = /gassorter

            if (parts.Length == 1)
            {
                MyAPIGateway.Utilities.ShowMessage(GSTags.ChatPrefix, "Commands: /gassorter debug [on|off]");
                return;
            }

            if (parts.Length >= 2 && parts[1].Equals(GSTags.CmdDebug, StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 2)
                {
                    MyAPIGateway.Utilities.ShowMessage(
                        GSTags.ChatPrefix,
                        $"Debug is {(DebugEnabled ? "ON" : "OFF")}. Use: /gassorter debug on|off");
                    return;
                }

                if (parts[2].Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    DebugEnabled = true;
                    MyAPIGateway.Utilities.ShowMessage(GSTags.ChatPrefix, "Debug ON");
                    return;
                }

                if (parts[2].Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    DebugEnabled = false;
                    MyAPIGateway.Utilities.ShowMessage(GSTags.ChatPrefix, "Debug OFF");
                    return;
                }

                MyAPIGateway.Utilities.ShowMessage(GSTags.ChatPrefix, "Usage: /gassorter debug on|off");
                return;
            }

            MyAPIGateway.Utilities.ShowMessage(GSTags.ChatPrefix, "Unknown command. Try: /gassorter debug on|off");
        }

        protected override void UnloadData()
        {
            if (_uiInitialized && MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.MessageEntered -= OnMessageEntered;

            if (_uiInitialized && MyAPIGateway.TerminalControls != null)
                MyAPIGateway.TerminalControls.CustomControlGetter -= TerminalControlGetter;

            _infoHooked.Clear();
        }

        private void UpdateLiveInfoRefresh()
        {
            _infoRefreshTick++;

            // Only refresh 2 times per second
            if (_infoRefreshTick < 30)
                return;

            _infoRefreshTick = 0;

            // Only do this if a terminal screen is open
            if (MyAPIGateway.Gui.GetCurrentScreen == null)
                return;

            // Refresh info for all gas-sorters we hooked
            foreach (var id in _infoHooked)
            {
                var ent = MyAPIGateway.Entities.GetEntityById(id) as IMyTerminalBlock;
                if (ent != null)
                    ent.RefreshCustomInfo();
            }
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

            // Optional: dump control IDs once when debug is enabled (helps you lock onto exact Drain All ID)
            if (DebugEnabled && !_dumpedSorterControls)
            {
                _dumpedSorterControls = true;
                DumpSorterControlIdsOnce(controls);
            }

            // Try to reorder until it succeeds. The first time this runs, our control may not yet
            // appear in the list (terminal builds list before AddControl takes effect).
            if (!_reorderedForSorterControls)
            {
                _reorderedForSorterControls = MoveGasControlUnderDrainAll(controls);
            }
            else
            {
                // Keep it in the right spot in case another mod reorders later
                MoveGasControlUnderDrainAll(controls);
            }
        }


        private static void DumpSorterControlIdsOnce(List<IMyTerminalControl> controls)
        {
            if (controls == null) return;

            // Keep it short: only show potential matches for "Drain" and our own control
            for (int i = 0; i < controls.Count; i++)
            {
                var c = controls[i];
                if (c == null || c.Id == null) continue;

                if (c.Id.IndexOf("Drain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.Id == "GasControlEnabled")
                {
                    MyAPIGateway.Utilities.ShowMessage(GSTags.ChatPrefixDbg, $"CTRL[{i}]: {c.Id} ({c.GetType().Name})");
                }
            }
        }

        private static bool MoveGasControlUnderDrainAll(List<IMyTerminalControl> controls)
        {
            if (controls == null || controls.Count == 0)
                return false;

            // Find our control
            int gasIdx = controls.FindIndex(c => c != null && c.Id == "GasControlEnabled");
            if (gasIdx < 0)
                return false;

            // Find vanilla "Drain All" control. IDs vary across versions, so use heuristic.
            // Drain All is typically a BUTTON, and its Id usually contains Drain + All.
            int drainIdx = controls.FindIndex(c =>
                c != null &&
                c.Id != null &&
                c.Id.IndexOf("Drain", StringComparison.OrdinalIgnoreCase) >= 0 &&
                c.Id.IndexOf("All", StringComparison.OrdinalIgnoreCase) >= 0);

            if (drainIdx < 0)
            {
                // Fallback: just place near the top of the list, after a few vanilla controls
                drainIdx = System.Math.Min(5, controls.Count - 1);
            }

            // If already directly after drainIdx, nothing to do
            if (gasIdx == drainIdx + 1)
                return true;

            var gasCtrl = controls[gasIdx];
            controls.RemoveAt(gasIdx);

            // If we removed something before drainIdx, the drain index shifts left
            if (gasIdx < drainIdx)
                drainIdx--;

            int insertAt = System.Math.Min(drainIdx + 1, controls.Count);
            controls.Insert(insertAt, gasCtrl);

            return true;
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
            checkbox.Setter = (block, value) =>
            {
                SetGasControlEnabled(block, value);

                // Immediate UI nudge
                GSTags.ForceTerminalInfoRefresh(block);
            };
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
            GSTags.ForceTerminalInfoRefresh(block);
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
                return;
            }

            sb.AppendLine("  Mode: Directional valve");

            // Show which gases this sorter is set up to handle, based on the fake gas items in its filter list.
            var gasMode = GasSorterGasLogic.GetSorterGasFilterMode(sorter);
            switch (gasMode)
            {
                case GasSorterGasLogic.GasFilterMode.OxygenOnly:
                    sb.AppendLine("  Gas Filter: Oxygen only");
                    break;

                case GasSorterGasLogic.GasFilterMode.HydrogenOnly:
                    sb.AppendLine("  Gas Filter: Hydrogen only");
                    break;

                case GasSorterGasLogic.GasFilterMode.Both:
                    sb.AppendLine("  Gas Filter: Oxygen + Hydrogen");
                    break;

                case GasSorterGasLogic.GasFilterMode.None:
                default:
                    sb.AppendLine("  Gas Filter: (none / items only)");
                    break;
            }
        }
    }
}
