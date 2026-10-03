// MapStitcher.Business/Contracts/ISheetArrangementOrchestrator.cs
using MapStitcher.Model;

namespace MapStitcher.Business.Contracts
{
    public interface ISheetArrangementOrchestrator
    {
        Task<CadSheetGridResult> ArrangeAsync(string directoryPath);

        /// <summary>Same as ArrangeAsync, but also stitches every placed sheet's
        /// full geometry (translated into a shared mosaic layout derived from
        /// grid Row/Column and each sheet's neatline size) into a single master
        /// DXF written under outputRootPath. Sets CadSheetGridResult.MergeOutputFileName
        /// on success; merge failures are appended to MergeErrors without
        /// affecting the grid layout itself.</summary>
        Task<CadSheetGridResult> ArrangeAsync(string directoryPath, string outputRootPath);
    }
}