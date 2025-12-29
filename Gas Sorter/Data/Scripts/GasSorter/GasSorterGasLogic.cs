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
        /// applies basic gas transfer, and logs what it sees.
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

                        // Only care about working, functional sorters
                        if (!sorter.IsWorking || !sorter.IsFunctional)
                            continue;

                        activeSorters++;

                        // For each active sorter, compute neighbors and handle gas + debug
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
        /// in grid coordinates, applies basic gas transfer, then logs what it sees.
        /// </summary>
        private static void DescribeSorterSides(
            IMyCubeGrid grid,
            IMySlimBlock slimSorter,
            IMyConveyorSorter sorter,
            int logicTick)
        {
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

            // Apply our basic gas rules (only on server)
            HandleGasTransfer(sorter, forwardSlim, backwardSlim);

            // Debug output so we can see what's on each side
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

        /// <summary>
        /// Basic phase-1 gas transfer:
        /// - If exactly one side is a tank, move a small amount per tick
        ///   in the arrow direction.
        /// - If both or neither sides are tanks, do nothing (for now).
        /// </summary>
        private static void HandleGasTransfer(IMyConveyorSorter sorter, IMySlimBlock forward, IMySlimBlock backward)
        {
            // Only the server should change game state
            if (MyAPIGateway.Multiplayer != null && !MyAPIGateway.Multiplayer.IsServer)
                return;

            var tankFwd = (forward != null) ? forward.FatBlock as Sandbox.ModAPI.IMyGasTank : null;
            var tankBack = (backward != null) ? backward.FatBlock as Sandbox.ModAPI.IMyGasTank : null;

            // Case 1: tank on forward side, NOT tank on back => push tank -> system
            if (tankFwd != null && tankBack == null)
            {
                double amt = 0.0005; // VERY small per tick for testing
                double current = tankFwd.FilledRatio;

                if (current > amt)
                {
                    tankFwd.ChangeFilledRatio(-amt, true);
                    MyAPIGateway.Utilities.ShowMessage(
                        "GasSorter",
                        $"Drain {tankFwd.CustomName}: -{amt:F4}");
                }
                return;
            }

            // Case 2: tank on backward side, NOT tank on forward => pull system -> tank
            if (tankBack != null && tankFwd == null)
            {
                double amt = 0.0005;
                double current = tankBack.FilledRatio;

                if (current < 0.999) // space to add
                {
                    tankBack.ChangeFilledRatio(amt, true);
                    MyAPIGateway.Utilities.ShowMessage(
                        "GasSorter",
                        $"Fill {tankBack.CustomName}: +{amt:F4}");
                }
                return;
            }

            // Case 3+: other configurations ignored for now
            // (two tanks, zero tanks; we leave untouched in phase 1)
        }
    }
}
