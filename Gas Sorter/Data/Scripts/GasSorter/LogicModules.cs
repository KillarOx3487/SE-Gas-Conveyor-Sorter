using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using System;

namespace GasSorter
{
    /// <summary>
    /// Shared context passed to every module.
    /// Keep this stable so modules stay isolated and forward-compatible.
    /// </summary>
    public struct GasSorterModuleContext
    {
        public IMyConveyorSorter Sorter;
        public IMyCubeGrid Grid;

        public IMySlimBlock SorterSlim;
        public IMySlimBlock ForwardSlim;
        public IMySlimBlock BackwardSlim;

        public GasSorterGasLogic.GasFilterMode FilterMode;

        public int LogicTick;
    }

    /// <summary>
    /// All logic modules implement this.
    /// </summary>
    public interface IGasSorterModule
    {
        string Name { get; }

        /// <summary>
        /// Set false to disable the module without removing code.
        /// </summary>
        bool Enabled { get; }

        /// <summary>
        /// 0 = run every dispatcher tick.
        /// Otherwise runs when (LogicTick % TickInterval) == 0.
        /// </summary>
        int TickInterval { get; }

        void Apply(ref GasSorterModuleContext ctx);
    }

    /// <summary>
    /// Wrapper module around your existing stable Tanks logic.
    /// This lets Tanks stay in its own file and never be edited for new features.
    /// </summary>
    public sealed class GasSorterTanksModule : IGasSorterModule
    {
        public string Name => "Tanks";
        public bool Enabled => true;

        // Tanks can run frequently; it's tiny and already server-gated.
        // Set to 0 for every tick, or e.g. 30 for once per second (if you want).
        public int TickInterval => 0;

        public void Apply(ref GasSorterModuleContext ctx)
        {
            GasSorterTanksLogic.Apply(
                ctx.Sorter,
                ctx.ForwardSlim,
                ctx.BackwardSlim,
                ctx.FilterMode
            );
        }
    }
}
