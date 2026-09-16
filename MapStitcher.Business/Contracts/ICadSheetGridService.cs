using MapStitcher.Model;

namespace MapStitcher.Business.Contracts
{
    public interface ICadSheetGridService
    {
        Task<CadSheetGridResult> BuildGridAsync(string directoryPath);

        Task MergeGridAsync(CadSheetGridResult result, string outputDirectory);
    }
}