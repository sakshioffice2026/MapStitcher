using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapStitcher.Database
{
    public class TiePoint
    {
        public int PointID { get; set; }
        public int SheetID { get; set; }
        public string? PointLabel { get; set; }
        public double SourceX { get; set; }
        public double SourceY { get; set; }
        public double? TargetX { get; set; }
        public double? TargetY { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        public SurveySheet SurveySheet { get; set; } = null!;
    }
}
