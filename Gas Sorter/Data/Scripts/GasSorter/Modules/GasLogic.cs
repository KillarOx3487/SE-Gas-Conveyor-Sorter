using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;
using System;
using System.Collections.Generic;
using GasSorter.Shared;
using GasSorter.Modules;

namespace GasSorter.Modules
{
    /// <summary>
    /// Orchestrator: finds active gas-control sorters, identifies neighbors,
    /// evaluates filter state, then dispatches to specific logic modules.
    /// </summary>
    public static class GasSorterGasLogic
    {
        // HashSet because SE API's GetEntities expects HashSet
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

        // ---- MODULE REGISTRY ----
        // Fixed order list of modules. Add new modules here without touching existing ones.
        private static readonly List<IGasSorterModule> _modules = new List<IGasSorterModule>();

        // Static constructor runs once
        static GasSorterGasLogic()
        {
            // Baseline: stable tank-to-tank behavior first
            _modules.Add(new GasSorterTanksModule());

            // Debug module (switchable)
            _modules.Add(new GasSorterDebugModule());

            // Future modules go here:
            // _modules.Add(new GasSorterGasGenModule());
            // _modules.Add(new GasSorterThrusterModule());
        }

        /// <summary>
        /// Called from GasSorterSession every N ticks.
        /// </summary>
        public static void RunGasControlScan(int logicTick)
        {
            bool doDebugBatch = false;

            try
            {
                if (MyAPIGateway.Entities == null)
                    return;

                // Start debug batch only when it would run (interval gating)
                if (GasSorterSession.DebugEnabled)
                {
                    // Match the Debug module interval
                    // (keeps debug overhead very low)
                    if (logicTick % 300 == 0)
                    {
                        doDebugBatch = true;
                        GasSorterDebugModule.BeginScan(logicTick);
                    }
                }
        
                _entityBuffer.Clear();
                MyAPIGateway.Entities.GetEntities(_entityBuffer, e => e is IMyCubeGrid);

                foreach (var ent in _entityBuffer)
                {
                    var grid = ent as IMyCubeGrid;
                    if (grid == null)
                        continue;

                    _slimBuffer.Clear();
                    grid.GetBlocks(_slimBuffer, slim => slim != null && slim.FatBlock is IMyConveyorSorter);

                    for (int i = 0; i < _slimBuffer.Count; i++)
                    {
                        var slim = _slimBuffer[i];
                        var sorter = slim.FatBlock as IMyConveyorSorter;
                        if (sorter == null)
                            continue;

                        // Only care about sorters with Gas Control enabled
                        if (!GasSorterSession.GetGasControlEnabled(sorter))
                            continue;

                        // Respect functional state; modules can also check Enabled / IsWorking if needed
                        if (!sorter.IsFunctional)
                            continue;

                        // Compute neighbors and dispatch
                        ProcessSorter(grid, slim, sorter, logicTick);
                    }
                }
            }
            catch (Exception e)
            {
                // Keep the scan alive even if something unexpected happens.
                if (MyAPIGateway.Utilities != null)
                    MyAPIGateway.Utilities.ShowMessage("GasSorter", $"Gas scan error: {e.Message}");
            }
            finally
            {
                if (doDebugBatch)
                    GasSorterDebugModule.EndScan();
            }
        }

        private static void ProcessSorter(
            IMyCubeGrid grid,
            IMySlimBlock slimSorter,
            IMyConveyorSorter sorter,
            int logicTick)
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

            // Build context once
            GasSorterModuleContext ctx = new GasSorterModuleContext
            {
                Sorter = sorter,
                Grid = grid,
                SorterSlim = slimSorter,
                ForwardSlim = forwardSlim,
                BackwardSlim = backwardSlim,
                FilterMode = filterMode,
                LogicTick = logicTick
            };

            // MODULE DISPATCH
            for (int m = 0; m < _modules.Count; m++)
            {
                var module = _modules[m];
                if (module == null || !module.Enabled)
                    continue;

                int interval = module.TickInterval;
                if (interval > 0 && (logicTick % interval) != 0)
                    continue;

                try
                {
                    module.Apply(ref ctx);
                }
                catch (Exception e)
                {
                    // Module-local failure should never kill the whole scan.
                    if (MyAPIGateway.Utilities != null)
                        MyAPIGateway.Utilities.ShowMessage("GasSorter", $"Module '{module.Name}' error: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Reads the sorter's filter list and infers fake-gas selection.
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

                if (subtype.IndexOf("Oxygen", StringComparison.OrdinalIgnoreCase) >= 0)
                    hasO = true;

                if (subtype.IndexOf("Hydrogen", StringComparison.OrdinalIgnoreCase) >= 0)
                    hasH = true;
            }

            if (hasO && hasH) return GasFilterMode.Both;
            if (hasO) return GasFilterMode.OxygenOnly;
            if (hasH) return GasFilterMode.HydrogenOnly;
            return GasFilterMode.None;
        }

        // Optional utility if you ever want neighbor descriptions again
        private static string DescribeNeighborBlock(IMySlimBlock slim)
        {
            if (slim == null || slim.FatBlock == null) return "none";

            var block = slim.FatBlock;
            if (block is Sandbox.ModAPI.IMyGasTank) return "GasTank";

            MyDefinitionId defId = block.BlockDefinition;
            string subtype = defId.SubtypeName;
            if (!string.IsNullOrWhiteSpace(subtype)) return subtype;

            string name = block.DefinitionDisplayNameText;
            if (!string.IsNullOrWhiteSpace(name)) return name;

            return "other";
        }
    }
}
