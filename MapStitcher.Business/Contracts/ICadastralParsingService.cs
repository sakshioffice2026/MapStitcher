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
        }
    }

