using VRage.Game.ModAPI;
using System;

namespace GasSorter.Shared.Backend
{
    /// <summary>
    /// Shared constants and "tags" used across the GasSorter script assembly.
    /// Keep this file stable to avoid touching core logic files for simple tweaks.
    /// </summary>
    internal static class GSTags
    {
        // Chat commands
        internal const string ChatRoot = "/gassorter";
        internal const string CmdDebug = "debug";

        // CustomData keys
        internal const string CustomDataKey_GasControl = "[GasSorter]GasControl=";

        // Chat message prefixes
        internal const string ChatPrefix = "GasSorter";
        internal const string ChatPrefixDbg = "GasSorterDbg";

        /// <summary>
        /// Forces the terminal Custom Info panel to update immediately.
        /// This matches vanilla + BuildInfo behavior.
        /// </summary>
        internal static void ForceTerminalInfoRefresh(IMyTerminalBlock block)
        {
            if (block == null)
                return;

            block.RefreshCustomInfo();

            // Some SE builds require this to force a repaint
            try
            {
                block.SetDetailedInfoDirty();
            }
            catch
            {
            // Method not present or not allowed on this block — safe to ignore
            }
        }
    }
}
