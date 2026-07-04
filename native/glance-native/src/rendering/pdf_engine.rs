// PDF rendering engine wrapper using pdfium-render
use pdfium_render::prelude::*;
use image::ImageFormat;
use std::io::Cursor;
use serde::{Serialize, Deserialize};

pub struct PdfEngineImpl {
    document: PdfDocument<'static>,
    _pdfium: Pdfium,
}

#[derive(Serialize, Deserialize, Debug, Clone)]
pub struct SearchMatch {
    #[serde(rename = "pageIndex")]
    pub page_index: u32,
    pub rects: Vec<MatchRect>,
}

#[derive(Serialize, Deserialize, Debug, Clone)]
pub struct MatchRect {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

impl PdfEngineImpl {
    /// Load a PDF file and create a rendering engine
    pub fn new(pdf_path: &str) -> Result<Self, String> {
        let exe_dir_pdfium = std::env::current_exe()
            .ok()
            .and_then(|mut p| {
                p.pop(); // Remove executable name
                p.push("pdfium.dll");
                Some(p)
            });

        let mut pdfium_result = None;
        if let Some(ref path) = exe_dir_pdfium {
            if let Some(path_str) = path.to_str() {
                if let Ok(bindings) = Pdfium::bind_to_library(path_str) {
                    pdfium_result = Some(bindings);
                }
            }
        }

        let pdfium = match pdfium_result {
            Some(bindings) => Pdfium::new(bindings),
            None => Pdfium::new(
                Pdfium::bind_to_library("pdfium.dll")
                    .or_else(|_| Pdfium::bind_to_library("bin/pdfium.dll"))
                    .or_else(|_| Pdfium::bind_to_system_library())
                    .map_err(|e| format!("Failed to load pdfium.dll: {:?}", e))?
            )
        };
        
        let document = pdfium.load_pdf_from_file(pdf_path, None)
            .map_err(|e| format!("Failed to load PDF file '{}': {:?}", pdf_path, e))?;
            
        // Safe transmute of PdfDocument lifetime to 'static since we hold the Pdfium instance
        // inside the same struct and pdfium will be dropped after the document.
        let document: PdfDocument<'static> = unsafe {
            std::mem::transmute(document)
        };
        
        Ok(PdfEngineImpl { document, _pdfium: pdfium })
    }

    /// Get the total number of pages in the document
    pub fn get_page_count(&self) -> u32 {
        self.document.pages().len() as u32
    }

    /// Get the dimensions of a page in points (1/72 inch)
    pub fn get_page_dimensions(&self, page_index: u32) -> Result<(f32, f32), String> {
        let pages = self.document.pages();
        if page_index >= pages.len() as u32 {
            return Err(format!("Page index {} out of range", page_index));
        }
        let page = pages.get(page_index as u16)
            .map_err(|e| format!("Failed to get page: {:?}", e))?;
        Ok((page.width().value, page.height().value))
    }

    /// Render a page to PNG bytes at the specified dimensions
    pub fn render_page_to_png(
        &self,
        page_index: u32,
        width: u32,
        height: u32,
        _dpi: f32,
    ) -> Result<Vec<u8>, String> {
        let pages = self.document.pages();
        if page_index >= pages.len() as u32 {
            return Err(format!("Page index {} out of range", page_index));
        }
        let page = pages.get(page_index as u16)
            .map_err(|e| format!("Failed to get page: {:?}", e))?;
        
        let render_config = PdfRenderConfig::new()
            .set_target_width(width as i32)
            .set_target_height(height as i32);
            
        let bitmap = page.render_with_config(&render_config)
            .map_err(|e| format!("Failed to render page: {:?}", e))?;
            
        let dynamic_image = bitmap.as_image();
            
        let mut buffer = Cursor::new(Vec::new());
        dynamic_image.write_to(&mut buffer, ImageFormat::Png)
            .map_err(|e| format!("Failed to write PNG: {:?}", e))?;
            
        Ok(buffer.into_inner())
    }

    /// Search for text occurrences across the document pages and return character bounds converted to UI coordinates
    pub fn search_text(&self, query: &str) -> Result<Vec<SearchMatch>, String> {
        let mut matches = Vec::new();
        let pages = self.document.pages();
        let search_options = PdfSearchOptions::new().match_case(false);
        
        for i in 0..pages.len() {
            let page = pages.get(i as u16)
                .map_err(|e| format!("Failed to get page {}: {:?}", i, e))?;
            
            let text_page = page.text()
                .map_err(|e| format!("Failed to get text for page {}: {:?}", i, e))?;
                
            let search = text_page.search(query, &search_options)
                .map_err(|e| format!("Failed to start search on page {}: {:?}", i, e))?;
            let crop_offset = page.boundaries().crop()
                .map(|b| (b.bounds.left().value as f64, b.bounds.bottom().value as f64))
                .unwrap_or((0.0, 0.0));

            let page_height = page.height().value as f64;
            
            for segments in search.iter(PdfSearchDirection::SearchForward) {
                let mut rects = Vec::new();
                for segment in segments.iter() {
                    let rect = segment.bounds();
                    
                    let abs_left = rect.left().value as f64;
                    let abs_top = rect.top().value as f64;
                    let abs_right = rect.right().value as f64;
                    let abs_bottom = rect.bottom().value as f64;

                    let rel_left = abs_left - crop_offset.0;
                    let rel_top = abs_top - crop_offset.1;
                    let rel_right = abs_right - crop_offset.0;
                    let rel_bottom = abs_bottom - crop_offset.1;

                    let x = rel_left;
                    let y = page_height - rel_top;
                    let width = rel_right - rel_left;
                    let height = rel_top - rel_bottom;
                    
                    rects.push(MatchRect { x, y, width, height });
                }
                
                if !rects.is_empty() {
                    matches.push(SearchMatch {
                        page_index: i as u32,
                        rects,
                    });
                }
            }
        }
        
        Ok(matches)
    }
}
