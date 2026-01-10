using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;
using System;
using System.Collections.Generic;

namespace GasSorter
{
    /// <summary>
    /// Orchestrator: finds active gas-control sorters, identifies neighbors,
    /// evaluates filter state, then dispatches to specific logic modules.
    /// </summary>
    public static class GasSorterGasLogic
    {
        // HashSet because this SE API's GetEntities expects HashSet<IMyEntity>
        private static readonly HashSet<IMyEntity> _entityBuffer = new HashSet<IMyEntity>();
        private static readonly List<IMySlimBlock> _slimBuffer = new List<IMySlimBlock>();

        // Public enums so modules can share them without needing extra shared files
        public enum GasFilterMode
        {
            None,
            OxygenOnly,
            HydrogenOnly,
            Both
        }

        public enum TankGasType
        {
            Unknown,
            Oxygen,
            Hydrogen
        }

        /// <summary>
        /// Called from GasSorterSession every N ticks.
        /// </summary>
        public static void RunGasControlScan(int logicTick)
        {
            try
            {
                if (MyAPIGateway.Entities == null)
                    return;

                _entityBuffer.Clear();
                MyAPIGateway.Entities.GetEntities(_entityBuffer, e => e is IMyCubeGrid);

                int totalGrids = 0;
                int totalSorters = 0;
                int activeSorters = 0;

                foreach (var ent in _entityBuffer)
                {
                    var grid = ent as IMyCubeGrid;
                    if (grid == null)
                        continue;

                    totalGrids++;

                    _slimBuffer.Clear();
                    grid.GetBlocks(_slimBuffer, slim => slim != null && slim.FatBlock is IMyConveyorSorter);

                    foreach (var slim in _slimBuffer)
                    {
                        var sorter = slim.FatBlock as IMyConveyorSorter;
                        if (sorter == null)
                            continue;

                        totalSorters++;

                        // Only care about sorters with Gas Control enabled
                        if (!GasSorterSession.GetGasControlEnabled(sorter))
                            continue;

                        // Respect functional state; modules can also check Enabled / IsWorking if needed
                        if (!sorter.IsFunctional)
                            continue;

                        activeSorters++;

                        // Compute neighbors and dispatch
                        ProcessSorter(grid, slim, sorter, logicTick);
                    }
                }

                // Optional summary; keep if you still like seeing it.
                if (logicTick % (60 * 5) == 0)
                {
                    MyAPIGateway.Utilities.ShowMessage(
                        "GasSorter",
                        $"Scan: grids={totalGrids}, sorters={totalSorters}, activeGasSorters={activeSorters}");
                }
            }
            catch (Exception e)
            {
                MyAPIGateway.Utilities.ShowMessage("GasSorter", $"Gas scan error: {e.Message}");
            }
        }

        private static void ProcessSorter(IMyCubeGrid grid, IMySlimBlock slimSorter, IMyConveyorSorter sorter, int logicTick)
        {
            // Determine forward/back positions using block orientation
            Vector3I sorterPos = slimSorter.Position;
            var orientation = sorter.Orientation;

            var forwardDir = orientation.Forward;
            Vector3I forwardOffset = Base6Directions.GetIntVector(forwardDir);

            var backwardDir = Base6Directions.GetFlippedDirection(forwardDir);
            Vector3I backwardOffset = Base6Directions.GetIntVector(backwardDir);

            Vector3I forwardPos = sorterPos + forwardOffset;
            Vector3I backwardPos = sorterPos + backwardOffset;

            IMySlimBlock forwardSlim = grid.GetCubeBlock(forwardPos);
            IMySlimBlock backwardSlim = grid.GetCubeBlock(backwardPos);

            // Compute filter mode once here, pass to modules
            GasFilterMode filterMode = GetSorterGasFilterMode(sorter);

            // MODULE DISPATCH:
            // Tank behavior (safe, stable)
            GasSorterTanksLogic.Apply(sorter, forwardSlim, backwardSlim, filterMode);

            // Future modules go here, without touching tank code:
            // GasSorterGasGenLogic.Apply(sorter, forwardSlim, backwardSlim, filterMode);
            // GasSorterThrusterLogic.Apply(...);

            // Optional per-sorter debug (throttle if needed)
            // Uncomment if you want occasional neighbor prints:
            /*
            if ((logicTick + (int)(sorter.EntityId & 0xFF)) % (60 * 5) == 0)
            {
                string sorterName = string.IsNullOrWhiteSpace(sorter.CustomName) ? sorter.DefinitionDisplayNameText : sorter.CustomName;
                MyAPIGateway.Utilities.ShowMessage("GasSorter",
                    $"Sorter '{sorterName}': Fwd={DescribeNeighborBlock(forwardSlim)}, Back={DescribeNeighborBlock(backwardSlim)} Filter={filterMode}");
            }
            */
        }

        /// <summary>
        /// Reads the sorter's filter list and infers fake-gas selection.
        /// No Sandbox.ModAPI.Ingame "using" required; we fully qualify the filter type.
        /// </summary>
        public static GasFilterMode GetSorterGasFilterMode(IMyConveyorSorter sorter)
        {
            if (sorter == null)
                return GasFilterMode.None;

            var filters = new List<Sandbox.ModAPI.Ingame.MyInventoryItemFilter>();
            sorter.GetFilterList(filters);

            bool hasO = false;
            bool hasH = false;

            for (int i = 0; i < filters.Count; i++)
            {
                var def = filters[i].ItemId;
                var subtype = def.SubtypeName;

                if (string.IsNullOrEmpty(subtype))
                    continue;

                if (subtype.IndexOf("Oxygen", StringComparison.OrdinalIgnoreCase) >= 0) hasO = true;
                if (subtype.IndexOf("Hydrogen", StringComparison.OrdinalIgnoreCase) >= 0) hasH = true;
            }

            if (hasO && hasH) return GasFilterMode.Both;
            if (hasO) return GasFilterMode.OxygenOnly;
            if (hasH) return GasFilterMode.HydrogenOnly;
            return GasFilterMode.None;
        }

        // Optional utility if you ever want neighbor descriptions again
        private static string DescribeNeighborBlock(IMySlimBlock slim)
        {
            if (slim == null || slim.FatBlock == null)
                return "none";

            var block = slim.FatBlock;

            if (block is Sandbox.ModAPI.IMyGasTank)
                return "GasTank";

            MyDefinitionId defId = block.BlockDefinition;
            string subtype = defId.SubtypeName;
            if (!string.IsNullOrWhiteSpace(subtype))
                return subtype;

            string name = block.DefinitionDisplayNameText;
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            return "other";
        }
    }
}
