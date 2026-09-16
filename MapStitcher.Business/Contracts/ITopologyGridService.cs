// MapStitcher.Business/Contracts/ITopologyGridService.cs
using MapStitcher.Model;

namespace MapStitcher.Business.Contracts
{
    public interface ITopologyGridService
    {
        TopologyBuildResult BuildGrid(List<SheetTopologyInput> inputs);
    }
}