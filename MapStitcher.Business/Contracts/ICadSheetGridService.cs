using MapStitcher.Model;

namespace MapStitcher.Business.Contracts
{
    public interface ICadSheetGridService
    {
        Task<CadSheetGridResult> BuildGridAsync(string directoryPath);

        Task MergeGridAsync(CadSheetGridResult result, string outputDirectory);

        // Full export pipeline: places every successfully arranged sheet
        // into its grid slot, leaves missing slots blank, reconstructs the
        // master village boundary from each sheet's Poly_Village_Bndry
        // geometry and trims the stitched perimeter to it, then writes the
        // result as a single unified DXF (and DWG).
        Task MergeVillageAlignedGridAsync(CadSheetGridResult result, string outputDirectory);
    }
}