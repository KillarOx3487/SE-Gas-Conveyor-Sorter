using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using System;
using VRage.Game;

namespace GasSorter
{
    /// <summary>
    /// Tank module: stable tank-to-tank directional transfer only.
    /// Does NOT attempt to simulate whole gas network.
    /// </summary>
    public static class GasSorterTanksLogic
    {
        public static void Apply(
            IMyConveyorSorter sorter,
            IMySlimBlock forwardSlim,
            IMySlimBlock backwardSlim,
            GasSorterGasLogic.GasFilterMode filterMode)
        {
            // Server-only state changes
            if (MyAPIGateway.Multiplayer != null && !MyAPIGateway.Multiplayer.IsServer)
                return;

            // Respect sorter ON/OFF. (You specifically wanted OFF to behave vanilla.)
            if (!sorter.Enabled)
                return;

            // Optional: also require "working" (powered + functional)
            if (!sorter.IsWorking || !sorter.IsFunctional)
                return;

            var tankFwd = (forwardSlim != null) ? forwardSlim.FatBlock as Sandbox.ModAPI.IMyGasTank : null;
            var tankBack = (backwardSlim != null) ? backwardSlim.FatBlock as Sandbox.ModAPI.IMyGasTank : null;

            // We only operate in the safe case: tank-to-tank
            if (tankFwd == null || tankBack == null)
                return;

            // Respect fake gas filter selection by only operating on matching tank types.
            // If filterMode is None or Both, we allow.
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

            // Direction rule: Back -> Forward (sorter arrow direction)
            // Transfer a tiny fill-ratio amount per call.
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

            // Optional debug (commented out)
            // MyAPIGateway.Utilities.ShowMessage("GasSorter", $"Tank->Tank move {move:F6} (Back -> Fwd)");
        }

        private static GasSorterGasLogic.TankGasType GetTankGasType(Sandbox.ModAPI.IMyGasTank tank)
        {
            // Usually subtype contains Hydrogen/Oxygen
            MyDefinitionId id = tank.BlockDefinition;
            string subtype = id.SubtypeName;

            if (!string.IsNullOrEmpty(subtype))
            {
                if (subtype.IndexOf("Hydrogen", StringComparison.OrdinalIgnoreCase) >= 0)
                    return GasSorterGasLogic.TankGasType.Hydrogen;

                if (subtype.IndexOf("Oxygen", StringComparison.OrdinalIgnoreCase) >= 0)
                    return GasSorterGasLogic.TankGasType.Oxygen;
            }

            // Fallback to display name
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
