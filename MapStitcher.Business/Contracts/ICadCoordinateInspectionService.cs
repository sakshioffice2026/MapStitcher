using MapStitcher.Model;

namespace MapStitcher.Business.Contracts
{
    public interface ICadCoordinateInspectionService
    {
        Task<CadCoordinateInspectionResult> InspectFileAsync(
            string filePath,
            string originalFileName);
    }
}