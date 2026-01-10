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
        private int _infoRefreshTick = 0;

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
                
                MyAPIGateway.Utilities.MessageEntered += OnMessageEntered;

                MyAPIGateway.TerminalControls.CustomControlGetter += TerminalControlGetter;
            }

            if (MyAPIGateway.Session == null)
                return;

            // --- Call gas logic skeleton every ~30 ticks ---
            _logicTick++;
            if (_logicTick % 30 == 0) // roughly every 0.5s at 60 FPS
            {
                GasSorterGasLogic.RunGasControlScan(_logicTick);
                UpdateLiveInfoRefresh();
            }
        }

private void OnMessageEntered(string messageText, ref bool sendToOthers)
{
    if (string.IsNullOrWhiteSpace(messageText)) return;

    // Only handle our commands
    // Examples:
    //  /gassorter debug on
    //  /gassorter debug off
    //  /gassorter debug
    string msg = messageText.Trim();

    if (!msg.StartsWith("/gassorter", StringComparison.OrdinalIgnoreCase))
        return;

    // Do not broadcast our command to chat
    sendToOthers = false;

    var parts = msg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    // parts[0] = /gassorter

    if (parts.Length == 1)
    {
        MyAPIGateway.Utilities.ShowMessage("GasSorter", "Commands: /gassorter debug [on|off]");
        return;
    }

    if (parts.Length >= 2 && parts[1].Equals("debug", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length == 2)
        {
            MyAPIGateway.Utilities.ShowMessage("GasSorter", $"Debug is {(DebugEnabled ? "ON" : "OFF")}. Use: /gassorter debug on|off");
            return;
        }

        if (parts[2].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            DebugEnabled = true;
            MyAPIGateway.Utilities.ShowMessage("GasSorter", "Debug ON");
            return;
        }

        if (parts[2].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            DebugEnabled = false;
            MyAPIGateway.Utilities.ShowMessage("GasSorter", "Debug OFF");
            return;
        }

        MyAPIGateway.Utilities.ShowMessage("GasSorter", "Usage: /gassorter debug on|off");
        return;
    }

    MyAPIGateway.Utilities.ShowMessage("GasSorter", "Unknown command. Try: /gassorter debug on|off");
}

        protected override void UnloadData()
        {
            if (MyAPIGateway.TerminalControls != null)
            {
                MyAPIGateway.Utilities.MessageEntered -= OnMessageEntered;
                MyAPIGateway.TerminalControls.CustomControlGetter -= TerminalControlGetter;
            }

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
        // --- Debug Switch --- 
        public static bool DebugEnabled { get; private set; } = false;

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

        private enum GasFilterMode
        {
            None,
            OxygenOnly,
            HydrogenOnly,
            Both
        }

        /// <summary>
        /// Look at the sorter's item filter list and infer which gases
        /// (if any) it is meant to control, based on your fake gas items.
        /// This does NOT change any behavior, it's just for info text.
        /// </summary>
        private static GasFilterMode GetGasFilterMode(IMyConveyorSorter sorter)
        {
            if (sorter == null)
                return GasFilterMode.None;

            // Get the current filter list
            var filters = new List<Sandbox.ModAPI.Ingame.MyInventoryItemFilter>();
            sorter.GetFilterList(filters);

            if (filters.Count == 0)
                return GasFilterMode.None;

            bool hasOxygen = false;
            bool hasHydrogen = false;

            foreach (var f in filters)
            {
                MyDefinitionId def = f.ItemId;
                string subtype = def.SubtypeName;

                if (string.IsNullOrEmpty(subtype))
                    continue;

                // These checks assume your fake ingots use names like
                // "Oxygen Gas" and "Hydrogen Gas" (or at least contain
                // "Oxygen" / "Hydrogen" in the subtype).
                if (subtype.IndexOf("Oxygen", StringComparison.OrdinalIgnoreCase) >= 0)
                    hasOxygen = true;

                if (subtype.IndexOf("Hydrogen", StringComparison.OrdinalIgnoreCase) >= 0)
                    hasHydrogen = true;
            }

            if (hasOxygen && hasHydrogen) return GasFilterMode.Both;
            if (hasOxygen) return GasFilterMode.OxygenOnly;
            if (hasHydrogen) return GasFilterMode.HydrogenOnly;
            return GasFilterMode.None;
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
                
               // Show which gases this sorter is set up to handle,
               // based on the fake gas items in its filter list.
               var gasMode = GetGasFilterMode(sorter);
               switch (gasMode)
              {
                  case GasFilterMode.OxygenOnly:
                      sb.AppendLine("  Gas Filter: Oxygen only");
                      break;

                  case GasFilterMode.HydrogenOnly:
                      sb.AppendLine("  Gas Filter: Hydrogen only");
                      break;

                  case GasFilterMode.Both:
                      sb.AppendLine("  Gas Filter: Oxygen + Hydrogen");
                      break;

                  case GasFilterMode.None:
                  default:
                      sb.AppendLine("  Gas Filter: (none / items only)");
                      break;
              }
            }
        }
    }
}
