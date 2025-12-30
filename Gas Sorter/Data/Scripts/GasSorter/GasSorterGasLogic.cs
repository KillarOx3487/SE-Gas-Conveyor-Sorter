using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;
using System;
using System.Collections.Generic;

namespace GasSorter
{
    public static class GasSorterGasLogic
    {
        // HashSet here because this SE API's GetEntities expects HashSet<IMyEntity>
        private static readonly HashSet<IMyEntity> _entityBuffer = new HashSet<IMyEntity>();
        private static readonly List<IMySlimBlock> _slimBuffer = new List<IMySlimBlock>();

        /// <summary>
        /// Called from GasSorterSession every N ticks.
        /// Scans for active gas-control sorters, identifies blocks on each side,
        /// and logs what it sees. DOES NOT TOUCH GAS.
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

                        // Only care about functional sorters (we still don't touch gas)
                        if (!sorter.IsFunctional)
                            continue;

                        activeSorters++;

                        // For each active sorter, compute neighbors and print debug ONLY
                        DescribeSorterSides(grid, slim, sorter, logicTick);
                    }
                }

                // Summary every ~5 seconds so you know it's alive
                if (logicTick % (60 * 5) == 0) // about every 5 seconds
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

        /// <summary>
        /// For a given active sorter, finds what is on its "forward" and "backward" sides
        /// in grid coordinates, then logs what it sees (tanks or other). No gas manipulation.
        /// </summary>
        private static void DescribeSorterSides(
            IMyCubeGrid grid,
            IMySlimBlock slimSorter,
            IMyConveyorSorter sorter,
            int logicTick)
        {
            // If you want less spam, uncomment this throttle:
            // if ((logicTick + (int)(sorter.EntityId & 0xFF)) % (60 * 5) != 0)
            //     return;

            Vector3I sorterPos = slimSorter.Position;
            var orientation = sorter.Orientation; // MyBlockOrientation

            // Forward direction vector (arrow direction)
            var forwardDir = orientation.Forward;
            Vector3I forwardOffset = Base6Directions.GetIntVector(forwardDir);

            // Backward is just the flipped forward direction
            var backwardDir = Base6Directions.GetFlippedDirection(forwardDir);
            Vector3I backwardOffset = Base6Directions.GetIntVector(backwardDir);

            Vector3I forwardPos = sorterPos + forwardOffset;
            Vector3I backwardPos = sorterPos + backwardOffset;

            IMySlimBlock forwardSlim = grid.GetCubeBlock(forwardPos);
            IMySlimBlock backwardSlim = grid.GetCubeBlock(backwardPos);

            string gridName = grid.DisplayName ?? grid.Name ?? "Unnamed Grid";
            string sorterName = sorter.CustomName;
            if (string.IsNullOrWhiteSpace(sorterName))
                sorterName = sorter.DefinitionDisplayNameText;

            string forwardDesc = DescribeNeighborBlock(forwardSlim);
            string backwardDesc = DescribeNeighborBlock(backwardSlim);

            MyAPIGateway.Utilities.ShowMessage(
                "GasSorter",
                $"Sorter '{sorterName}' on grid '{gridName}': Fwd={forwardDesc}, Back={backwardDesc}");
        }

        /// <summary>
        /// Returns a short description of what kind of block is on a given side.
        /// For now we care about gas tanks and "none/other".
        /// </summary>
        private static string DescribeNeighborBlock(IMySlimBlock slim)
        {
            if (slim == null || slim.FatBlock == null)
                return "none";

            var block = slim.FatBlock;

            // We care about gas tanks explicitly
            if (block is Sandbox.ModAPI.IMyGasTank)
                return "GasTank";

            // Fallback: show subtype name if available
            MyDefinitionId defId = block.BlockDefinition; // SerializableDefinitionId/MyDefinitionId
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
