using pdf_extractor.Models;
using System.IO;
using System.Collections.Generic;

namespace pdf_extractor.Services
{
    public interface IPdfExtractor
    {
        OrderDocument ExtractOrder(Stream pdfStream);
    }
}