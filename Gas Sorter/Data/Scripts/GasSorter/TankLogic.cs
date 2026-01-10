using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game;

namespace GasSorter
{
    /// <summary>
    /// Tank module: stable tank-to-tank directional transfer only.
    /// Does NOT attempt to simulate whole gas network.
    /// Adds "anti-bounce" detection so we don't fight generators/thrusters.
    /// </summary>
    public static class GasSorterTanksLogic
    {
        // Track last seen FilledRatio per tank to detect active network activity
        private static readonly Dictionary<long, double> _lastRatio = new Dictionary<long, double>();

        // How much ratio change between scans counts as "active" (generator/thruster/etc. interacting)
        // Your scan is every ~30 ticks, so this can be pretty small.
        private const double ActiveDeltaThreshold = 0.0008;

        public static void Apply(
            IMyConveyorSorter sorter,
            IMySlimBlock forwardSlim,
            IMySlimBlock backwardSlim,
            GasSorterGasLogic.GasFilterMode filterMode)
        {
            // Server-only state changes
            if (MyAPIGateway.Multiplayer != null && !MyAPIGateway.Multiplayer.IsServer)
                return;

            // Respect sorter ON/OFF
            if (!sorter.Enabled)
                return;

            // Require working (powered + functional)
            if (!sorter.IsWorking || !sorter.IsFunctional)
                return;

            var tankFwd = (forwardSlim != null) ? forwardSlim.FatBlock as Sandbox.ModAPI.IMyGasTank : null;
            var tankBack = (backwardSlim != null) ? backwardSlim.FatBlock as Sandbox.ModAPI.IMyGasTank : null;

            // Safe case only: tank-to-tank
            if (tankFwd == null || tankBack == null)
                return;

            // Respect fake gas selection by only operating on matching tank types.
            // If filterMode is None or Both, allow.
            if (filterMode != GasSorterGasLogic.GasFilterMode.None &&
                filterMode != GasSorterGasLogic.GasFilterMode.Both)
            {
                var fType = GetTankGasType(tankFwd);
                var bType = GetTankGasType(tankBack);

                if (filterMode == GasSorterGasLogic.GasFilterMode.OxygenOnly)
                {
                    if (fType != GasSorterGasLogic.TankGasType.Oxygen || bType != GasSorterGasLogic.TankGasType.Oxygen)
                        return;
                }
                else if (filterMode == GasSorterGasLogic.GasFilterMode.HydrogenOnly)
                {
                    if (fType != GasSorterGasLogic.TankGasType.Hydrogen || bType != GasSorterGasLogic.TankGasType.Hydrogen)
                        return;
                }
            }

            // Anti-bounce: if either tank is changing due to the vanilla gas sim, do nothing this scan.
            // This prevents the generator/tank "ping pong" and negative-looking readouts.
            if (IsTankActive(tankFwd) || IsTankActive(tankBack))
                return;

            // Direction rule: Back -> Forward (sorter arrow direction)
            const double amt = 0.0002; // tune later

            double backRatio = tankBack.FilledRatio;
            double fwdRatio = tankFwd.FilledRatio;

            if (backRatio <= 0.0000001)
                return;

            if (fwdRatio >= 0.999999)
                return;

            double move = amt;
            if (move > backRatio) move = backRatio;
            if (move > (0.999999 - fwdRatio)) move = (0.999999 - fwdRatio);

            if (move <= 0)
                return;

            tankBack.ChangeFilledRatio(-move, true);
            tankFwd.ChangeFilledRatio(move, true);

            // Update last ratios after our own move so we don't flag ourselves as "active"
            _lastRatio[tankFwd.EntityId] = tankFwd.FilledRatio;
            _lastRatio[tankBack.EntityId] = tankBack.FilledRatio;
        }

        private static bool IsTankActive(Sandbox.ModAPI.IMyGasTank tank)
        {
            long id = tank.EntityId;
            double current = tank.FilledRatio;

            double last;
            if (_lastRatio.TryGetValue(id, out last))
            {
                double delta = Math.Abs(current - last);
                _lastRatio[id] = current;
                return delta > ActiveDeltaThreshold;
            }

            // First time we see it: seed and treat as not active
            _lastRatio[id] = current;
            return false;
        }

        private static GasSorterGasLogic.TankGasType GetTankGasType(Sandbox.ModAPI.IMyGasTank tank)
        {
            MyDefinitionId id = tank.BlockDefinition;
            string subtype = id.SubtypeName;

            if (!string.IsNullOrEmpty(subtype))
            {
                if (subtype.IndexOf("Hydrogen", StringComparison.OrdinalIgnoreCase) >= 0)
                    return GasSorterGasLogic.TankGasType.Hydrogen;

                if (subtype.IndexOf("Oxygen", StringComparison.OrdinalIgnoreCase) >= 0)
                    return GasSorterGasLogic.TankGasType.Oxygen;
            }

            string dn = tank.DefinitionDisplayNameText;
            if (!string.IsNullOrEmpty(dn))
            {
                if (dn.IndexOf("Hydrogen", StringComparison.OrdinalIgnoreCase) >= 0)
                    return GasSorterGasLogic.TankGasType.Hydrogen;

                if (dn.IndexOf("Oxygen", StringComparison.OrdinalIgnoreCase) >= 0)
                    return GasSorterGasLogic.TankGasType.Oxygen;
            }

            return GasSorterGasLogic.TankGasType.Unknown;
        }
    }
}
