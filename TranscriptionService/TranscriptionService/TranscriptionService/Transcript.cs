using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TranscriptionService
{
    public class Transcript
    {
        public string Id { get; set; }
        public DateTime CreatedOn { get; set; }
        public string FileUrl { get; set; }
        public DateTime RequestedOn { get; set; }
        public string VociRequestId { get; set; }
        public string VociTranscript { get; set; }
    }
}
