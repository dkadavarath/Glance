using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf.Annotations;
using Glance;

namespace Glance.Services;

public class PdfExportService
{
    public async Task ExportPdfWithAnnotationsAsync(
        string inputPdfPath, 
        string outputPdfPath, 
        List<SavedAnnotation> annotations,
        Dictionary<int, double>? pageRotations = null)
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
                // First, permanently apply any user-requested page rotations to the PDF file itself
                for (int i = 0; i < document.Pages.Count; i++)
                {
                    PdfPage p = document.Pages[i];
                    if (pageRotations != null && pageRotations.TryGetValue(i, out double userRot))
                    {
                        int nativeRotation = p.Rotate;
                        p.Rotate = (nativeRotation + (int)userRot) % 360;
                    }
                }

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
                    int currentRotation = page.Rotate;

                    // Temporarily reset rotation to 0 to draw on unrotated physical page coordinates
                    page.Rotate = 0;

                    try
                    {
                        double cropXOffset = 0;
                        double cropYOffset = 0;
                        double W_c = page.Width.Point;
                        double H_c = page.Height.Point;

                        try
                        {
                            if (page.Elements.ContainsKey("/CropBox") && page.CropBox != null)
                            {
                                var cropBox = page.CropBox;
                                cropXOffset = cropBox.X1;
                                cropYOffset = page.MediaBox.Height - cropBox.Y2;
                                W_c = cropBox.X2 - cropBox.X1;
                                H_c = cropBox.Y2 - cropBox.Y1;
                            }
                        }
                        catch
                        {
                            W_c = page.Width.Point;
                            H_c = page.Height.Point;
                        }

                        // XGraphics allows drawing directly on the PDF page graphics context
                        using (XGraphics gfx = XGraphics.FromPdfPage(page))
                        {
                            foreach (var anno in entry.Value)
                            {
                                int totalRotation = currentRotation;

                                if (anno.Type == AnnotationType.Highlight)
                                {
                                    XColor color = ParseColor(anno.ColorHex, 0.31); // 31% opacity for highlight
                                    XBrush brush = new XSolidBrush(color);
                                    XRect physicalRect = MapRectToPhysical(anno.X * 0.75, anno.Y * 0.75, anno.Width * 0.75, anno.Height * 0.75, totalRotation, W_c, H_c, cropXOffset, cropYOffset);
                                    gfx.DrawRectangle(brush, physicalRect);
                                }
                                else if (anno.Type == AnnotationType.Pen && anno.Points.Count > 1)
                                {
                                    XColor color = ParseColor(anno.ColorHex, 1.0); // Solid color for pen drawing
                                    double penThickness = (anno.Thickness > 0 ? anno.Thickness : 3.5) * 0.75;
                                    XPen pen = new XPen(color, penThickness);
                                    pen.LineCap = XLineCap.Round;
                                    pen.LineJoin = XLineJoin.Round;

                                    for (int i = 0; i < anno.Points.Count - 1; i++)
                                    {
                                        XPoint p1 = MapPointToPhysical(anno.Points[i].X * 0.75, anno.Points[i].Y * 0.75, totalRotation, W_c, H_c, cropXOffset, cropYOffset);
                                        XPoint p2 = MapPointToPhysical(anno.Points[i + 1].X * 0.75, anno.Points[i + 1].Y * 0.75, totalRotation, W_c, H_c, cropXOffset, cropYOffset);
                                        gfx.DrawLine(pen, p1, p2);
                                    }
                                }
                                else if (anno.Type == AnnotationType.Note)
                                {
                                    XPoint pt = MapPointToPhysical(anno.X * 0.75, anno.Y * 0.75, totalRotation, W_c, H_c, cropXOffset, cropYOffset);

                                    // Add a native PDF text annotation
                                    try
                                    {
                                        PdfTextAnnotation textAnnot = new PdfTextAnnotation();
                                        textAnnot.Title = "Reader's Note";
                                        textAnnot.Contents = anno.Content;
                                        
                                        // Y coordinate in PDF annotations is bottom-up, so invert it
                                        double pdfY = page.MediaBox.Height - pt.Y;
                                        textAnnot.Rectangle = new PdfRectangle(new XRect(pt.X - 8, pdfY - 16, 24, 24));
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
                    finally
                    {
                        // Restore original page rotation
                        page.Rotate = currentRotation;
                    }
                }

                document.Save(outputPdfPath);
            }
        });
    }

    private static XRect MapRectToPhysical(
        double x_ui, double y_ui, double w_ui, double h_ui,
        int rotation,
        double W_c, double H_c,
        double cropXOffset, double cropYOffset)
    {
        double x_cropped, y_cropped, w_cropped, h_cropped;

        switch (rotation)
        {
            case 90:
                x_cropped = y_ui;
                y_cropped = H_c - x_ui - w_ui;
                w_cropped = h_ui;
                h_cropped = w_ui;
                break;
            case 180:
                x_cropped = W_c - x_ui - w_ui;
                y_cropped = H_c - y_ui - h_ui;
                w_cropped = w_ui;
                h_cropped = h_ui;
                break;
            case 270:
                x_cropped = W_c - y_ui - h_ui;
                y_cropped = x_ui;
                w_cropped = h_ui;
                h_cropped = w_ui;
                break;
            default: // 0
                x_cropped = x_ui;
                y_cropped = y_ui;
                w_cropped = w_ui;
                h_cropped = h_ui;
                break;
        }

        return new XRect(x_cropped + cropXOffset, y_cropped + cropYOffset, w_cropped, h_cropped);
    }

    private static XPoint MapPointToPhysical(
        double x_pt_ui, double y_pt_ui,
        int rotation,
        double W_c, double H_c,
        double cropXOffset, double cropYOffset)
    {
        double x_cropped, y_cropped;

        switch (rotation)
        {
            case 90:
                x_cropped = y_pt_ui;
                y_cropped = H_c - x_pt_ui;
                break;
            case 180:
                x_cropped = W_c - x_pt_ui;
                y_cropped = H_c - y_pt_ui;
                break;
            case 270:
                x_cropped = W_c - y_pt_ui;
                y_cropped = x_pt_ui;
                break;
            default: // 0
                x_cropped = x_pt_ui;
                y_cropped = y_pt_ui;
                break;
        }

        return new XPoint(x_cropped + cropXOffset, y_cropped + cropYOffset);
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
