using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapStitcher.Business.Contracts
{

    public interface ICadastralParsingService
    {
        Task ParseSheetAsync(int sheetId);

        /// <summary>
        /// Debug-only: returns every TEXT/MTEXT entity's raw, undecoded Value
        /// string from the sheet's CAD file, with its decoded (best-effort)
        /// form alongside. Used to extract exact Sakal Bharati raw sequences
        /// for verifying DxfUnicodeEscapeDecoder and layer names — never used
        /// in the parse or merge pipeline itself.
        /// </summary>
        Task<List<RawTextDump>> DumpRawTextAsync(int sheetId);
    }

    public class RawTextDump
    {
        public string EntityType { get; set; } = "";
        public string LayerName { get; set; } = "";
        public string RawValue { get; set; } = "";
        public string DecodedValue { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
    }
}