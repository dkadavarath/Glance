using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf.Annotations;
using FluentPdfViewer;

namespace Glance.Services;

public class PdfExportService
{
    public async Task ExportPdfWithAnnotationsAsync(string inputPdfPath, string outputPdfPath, List<SavedAnnotation> annotations)
    {
        await Task.Run(() =>
        {
            // Register encoding provider for older PDF parsing if needed
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            }
            catch { }

            using (PdfDocument document = PdfReader.Open(inputPdfPath, PdfDocumentOpenMode.Modify))
            {
                // Group annotations by page index
                var annotationsByPage = new Dictionary<int, List<SavedAnnotation>>();
                foreach (var anno in annotations)
                {
                    if (!annotationsByPage.ContainsKey(anno.PageIndex))
                    {
                        annotationsByPage[anno.PageIndex] = new List<SavedAnnotation>();
                    }
                    annotationsByPage[anno.PageIndex].Add(anno);
                }

                foreach (var entry in annotationsByPage)
                {
                    int pageIndex = entry.Key;
                    if (pageIndex < 0 || pageIndex >= document.Pages.Count) continue;

                    PdfPage page = document.Pages[pageIndex];

                    // XGraphics allows drawing directly on the PDF page graphics context
                    using (XGraphics gfx = XGraphics.FromPdfPage(page))
                    {
                        foreach (var anno in entry.Value)
                        {
                            if (anno.Type == AnnotationType.Highlight)
                            {
                                XColor color = ParseColor(anno.ColorHex, 0.31); // 31% opacity for highlight
                                XBrush brush = new XSolidBrush(color);
                                gfx.DrawRectangle(brush, anno.X, anno.Y, anno.Width, anno.Height);
                            }
                            else if (anno.Type == AnnotationType.Pen && anno.Points.Count > 1)
                            {
                                XColor color = ParseColor(anno.ColorHex, 1.0); // Solid color for pen drawing
                                XPen pen = new XPen(color, 3.5);
                                pen.LineCap = XLineCap.Round;
                                pen.LineJoin = XLineJoin.Round;

                                for (int i = 0; i < anno.Points.Count - 1; i++)
                                {
                                    gfx.DrawLine(pen, anno.Points[i].X, anno.Points[i].Y,
                                                     anno.Points[i + 1].X, anno.Points[i + 1].Y);
                                }
                            }
                            else if (anno.Type == AnnotationType.Note)
                            {
                                // Draw a comment icon on the page directly (as a visual fallback)
                                try
                                {
                                    XFont font = new XFont("Segoe UI Emoji", 14);
                                    gfx.DrawString("💬", font, XBrushes.DarkOrange, anno.X - 8, anno.Y + 8);
                                }
                                catch { }

                                // Add a native PDF text annotation
                                try
                                {
                                    PdfTextAnnotation textAnnot = new PdfTextAnnotation();
                                    textAnnot.Title = "Reader's Note";
                                    textAnnot.Contents = anno.Content;
                                    
                                    // Y coordinate in PDF annotations is bottom-up, so invert it
                                    double pdfY = page.Height.Point - anno.Y;
                                    textAnnot.Rectangle = new PdfRectangle(new XRect(anno.X - 8, pdfY - 16, 24, 24));
                                    textAnnot.Icon = PdfTextAnnotationIcon.Comment;
                                    page.Annotations.Add(textAnnot);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Failed to add native text annotation: {ex.Message}");
                                }
                            }
                        }
                    }
                }

                document.Save(outputPdfPath);
            }
        });
    }

    private static XColor ParseColor(string hex, double alpha = 1.0)
    {
        try
        {
            string cleanHex = hex.Replace("#", "");
            if (cleanHex.Length == 8)
            {
                byte a = Convert.ToByte(cleanHex.Substring(0, 2), 16);
                byte r = Convert.ToByte(cleanHex.Substring(2, 2), 16);
                byte g = Convert.ToByte(cleanHex.Substring(4, 2), 16);
                byte b = Convert.ToByte(cleanHex.Substring(6, 2), 16);
                return XColor.FromArgb(a, r, g, b);
            }
            else if (cleanHex.Length == 6)
            {
                byte r = Convert.ToByte(cleanHex.Substring(0, 2), 16);
                byte g = Convert.ToByte(cleanHex.Substring(2, 2), 16);
                byte b = Convert.ToByte(cleanHex.Substring(4, 2), 16);
                return XColor.FromArgb((int)(alpha * 255), r, g, b);
            }
        }
        catch { }
        return XColor.FromArgb((int)(alpha * 255), 255, 255, 0); // Default Yellow
    }
}
