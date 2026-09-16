// MapStitcher.Business/Contracts/ISheetArrangementOrchestrator.cs
using MapStitcher.Model;

namespace MapStitcher.Business.Contracts
{
    public interface ISheetArrangementOrchestrator
    {
        Task<CadSheetGridResult> ArrangeAsync(string directoryPath);
    }
}