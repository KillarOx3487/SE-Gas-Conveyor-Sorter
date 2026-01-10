using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;
using System;

namespace GasSorter
{
    /// <summary>
    /// Switchable debug output. No gameplay side effects.
    /// Enable/disable with /gassorter debug on|off
    /// </summary>
    public sealed class GasSorterDebugModule : IGasSorterModule
    {
        public string Name => "Debug";
        public bool Enabled => GasSorterSession.DebugEnabled;

        // Run this module occasionally to reduce spam.
        // Your scan likely runs around every 30 ticks; this means print once every 300 ticks (~5 sec).
        public int TickInterval => 300;

        // Aggregation across sorters per scan tick
        private static int _lastTick = -1;
        private static int _activeSortersThisTick = 0;

        public void Apply(ref GasSorterModuleContext ctx)
        {
            // Only print on server to avoid duplicate spam from clients
            if (MyAPIGateway.Multiplayer != null && !MyAPIGateway.Multiplayer.IsServer)
                return;

            int tick = ctx.LogicTick;

            // Reset counters when tick changes (first sorter processed that tick)
            if (_lastTick != tick)
            {
                _lastTick = tick;
                _activeSortersThisTick = 0;
            }

            _activeSortersThisTick++;

            // Throttle per-sorter details: only log a subset each print interval
            // Use EntityId hash to pick ~1/8 of sorters per print to keep it readable
            long id = ctx.Sorter?.EntityId ?? 0;
            if ((id & 0x7) != 0) // 7 = mask for 1/8 selection
                return;

            string sorterName = ctx.Sorter?.CustomName;
            if (string.IsNullOrWhiteSpace(sorterName))
                sorterName = ctx.Sorter?.DefinitionDisplayNameText ?? "Sorter";

            string fwd = Describe(ctx.ForwardSlim);
            string back = Describe(ctx.BackwardSlim);

            MyAPIGateway.Utilities.ShowMessage(
                "GasSorterDbg",
                $"[{tick}] '{sorterName}' Filter={ctx.FilterMode} Fwd={fwd} Back={back}"
            );

            // Print a small summary once per interval (only from the selected subset's first call)
            // We use the selection to avoid printing summary N times.
            if ((id & 0xFF) == 0) // very rare "leader" condition
            {
                MyAPIGateway.Utilities.ShowMessage(
                    "GasSorterDbg",
                    $"[{tick}] Active gas sorters processed this tick: {_activeSortersThisTick}"
                );
            }
        }

        private static string Describe(IMySlimBlock slim)
        {
            if (slim == null || slim.FatBlock == null) return "none";

            var fat = slim.FatBlock;

            // Most useful types first
            if (fat is Sandbox.ModAPI.IMyGasTank) return "GasTank";

            // Generic fallback
            string name = fat.DefinitionDisplayNameText;
            if (!string.IsNullOrWhiteSpace(name)) return name;

            return fat.GetType().Name;
        }
    }
}
