using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using WFC.Core;


namespace WFC.Burst {
    [BurstCompile(CompileSynchronously = true)]
    public unsafe struct WFCSolveJob : IJob {
        [NativeDisableUnsafePtrRestriction] public WFCKernelArgs args;

        public void Execute() { WFCKernel.Run(ref args); }
    }

    /// <summary>
    /// Runs the solver kernel as a burst-compiled job. Burst compiles the whole call graph from Execute()
    /// </summary>
    /// <remarks>
    /// Results are deterministic, but results may differ between mono & burst b/c float math differences between
    /// the two mattering for entropy calculations.
    /// </remarks>
    public sealed class BurstWFCExecutor : IWFCExecutor {
        public void Execute(ref WFCKernelArgs args) {
            var job = new WFCSolveJob { args = args };
            job.Run();
        }
    }
}
