// MapStitcher.Business/Contracts/INeatlineExtractionService.cs
using ACadSharp;
using MapStitcher.Model;

namespace MapStitcher.Business.Contracts
{
    public interface INeatlineExtractionService
    {
        NeatlineExtent Extract(CadDocument document, string neatlineLayerName);
    }
}