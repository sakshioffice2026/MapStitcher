using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapStitcher.Database
{
    public class Project
    {
        public int ProjectID { get; set; }
        public string Name { get; set; } = null!;
        public string District { get; set; } = null!;
        public string Taluka { get; set; } = null!;
        public string Village { get; set; } = null!;
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? UserID { get; set; }

        public ICollection<SurveySheet> SurveySheets { get; set; } = new List<SurveySheet>();
    }
}