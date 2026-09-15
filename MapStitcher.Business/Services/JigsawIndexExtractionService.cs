using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using MapStitcher.Business.Contracts;
using MapStitcher.Repositories.Contracts;
using MapStitcher.Utilities;
using System.Text.RegularExpressions;

namespace MapStitcher.Business.Services
{
    public class JigsawIndexExtractionService : IJigsawIndexExtractionService
    {
        private readonly ISurveySheetRepository _sheetRepo;
        private readonly string _uploadRootPath;

        // "Sheet Index No. 14", "Index : 14"
        private static readonly Regex IndexPattern =
            new(@"(?:Sheet\s*Index|Index)\s*(?:No\.?)?\s*[:\-]?\s*(\d+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "R2C5", "Row 2 Col 5"
        private static readonly Regex RowColPattern =
            new(@"R\s*(\d+)\s*C\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "Gat No. 245", "गट नं 245"
        private static readonly Regex GatPattern =
            new(@"(?:Gat|गट)\s*(?:No\.?|नं\.?)?\s*[:\-]?\s*(\d+[A-Za-z]?)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public JigsawIndexExtractionService(
            ISurveySheetRepository sheetRepo,
            string uploadRootPath)
        {
            _sheetRepo = sheetRepo;
            _uploadRootPath = uploadRootPath;
        }

        public async Task<SheetIndexInfo> ExtractAsync(int sheetId)
        {
            var sheet = await _sheetRepo.GetByIdAsync(sheetId)
                ?? throw new InvalidOperationException($"SurveySheet {sheetId} not found.");

            var info = new SheetIndexInfo
            {
                SheetID = sheet.SheetID,
                SheetNumber = sheet.SheetNumber
            };

            var fullPath = Path.Combine(_uploadRootPath, sheet.FilePath);
            if (!File.Exists(fullPath))
                return info;

            CadDocument document;
            try
            {
                var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                document = ext == ".dxf" ? DxfReader.Read(fullPath) : DwgReader.Read(fullPath);
            }
            catch
            {
                return info;
            }

            foreach (var entity in document.Entities)
            {
                string? raw = entity switch
                {
                    TextEntity t => t.Value,
                    MText m => m.Value,
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var decoded = DxfUnicodeEscapeDecoder.Decode(raw);

                if (!info.ExplicitRow.HasValue)
                {
                    var rc = RowColPattern.Match(decoded);
                    if (rc.Success)
                    {
                        info.ExplicitRow = int.Parse(rc.Groups[1].Value);
                        info.ExplicitCol = int.Parse(rc.Groups[2].Value);
                    }
                }

                if (!info.SheetIndexNumber.HasValue)
                {
                    var idx = IndexPattern.Match(decoded);
                    if (idx.Success)
                        info.SheetIndexNumber = int.Parse(idx.Groups[1].Value);
                }

                if (info.GatNumber == null)
                {
                    var gat = GatPattern.Match(decoded);
                    if (gat.Success)
                        info.GatNumber = gat.Groups[1].Value;
                }
            }

            info.Extracted = info.ExplicitRow.HasValue || info.SheetIndexNumber.HasValue;
            return info;
        }
    }
}