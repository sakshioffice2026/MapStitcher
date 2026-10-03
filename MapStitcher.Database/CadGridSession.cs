using System;
using System.Collections.Generic;

namespace MapStitcher.Database
{
    public class CadGridSession
    {
        // Matches CadGridController's Guid.NewGuid("N") session folder name
        public string SessionId { get; set; } = null!;

        public int SheetCount { get; set; }
        public int RowCount { get; set; }
        public int ColumnCount { get; set; }

        public string? MergeOutputFileName { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime? ExpiresAt { get; set; }

        public ICollection<CadGridSheet> Sheets { get; set; } = new List<CadGridSheet>();
        public ICollection<CadGridMissingSlot> MissingSlots { get; set; } = new List<CadGridMissingSlot>();
        public ICollection<CadGridMergeError> MergeErrors { get; set; } = new List<CadGridMergeError>();
        public ICollection<CadGridSkippedFile> SkippedFiles { get; set; } = new List<CadGridSkippedFile>();
    }
}
