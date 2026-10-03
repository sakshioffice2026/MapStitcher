// MapStitcher.Business/Contracts/IIndexMapExtractionService.cs
using MapStitcher.Model;

namespace MapStitcher.Business.Contracts
{
    public interface IIndexMapExtractionService
    {
        Task<IndexMapNeighbors> ExtractAsync(string filePath, string indexLayerName);
    }
}