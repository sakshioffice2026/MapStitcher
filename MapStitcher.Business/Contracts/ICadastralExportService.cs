//namespace MapStitcher.Business.Contracts
//{
//    public interface ICadastralExportService
//    {
//        Task<string> ExportProjectAsync(int projectId, string outputRootPath);

//        /// <summary>
//        /// Isolated download action, decoupled from preview/layout. Clones every
//        /// ModelSpace entity from each sheet's original DWG/DXF file (not just
//        /// the extracted boundary polygon), applies each sheet's persisted
//        /// OffsetX/OffsetY (set by ISheetGridArrangementService.ArrangeSheetsGrid),
//        /// and writes a fully populated master DXF. Throws if ArrangeSheetsGrid
//        /// has not been run yet, or if no sheet could be cloned, rather than
//        /// silently writing an empty file.
//        /// </summary>
//        Task<string> ExportMasterDxfAsync(int projectId, string outputRootPath);
//    }
//}