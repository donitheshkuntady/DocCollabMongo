using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DocCollabMongoCore
{
    public static class ApplicationConstant
    {
        public const int DocCollabSaveThreshold = 100;
        public const string DocumentCollabTempTablePrefix = "DocColMeta_";
        public const string DocumentCollabTempTableVersionInfo = "DocCol_Version_Info";
    }
}
