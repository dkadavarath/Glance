using System;
using System.Collections.Generic;

namespace Glance.Services
{
    public static class LocalizationService
    {
        public static string CurrentLanguage { get; private set; } = "en";

        static LocalizationService()
        {
            try
            {
                // Try to get language from Windows user profile preferences first
                var languages = Windows.System.UserProfile.GlobalizationPreferences.Languages;
                if (languages != null && languages.Count > 0)
                {
                    string primaryLang = languages[0];
                    if (primaryLang.StartsWith("es", StringComparison.OrdinalIgnoreCase))
                    {
                        CurrentLanguage = "es";
                        return;
                    }
                }
            }
            catch { }

            try
            {
                // Fallback to CultureInfo
                var cultureName = System.Globalization.CultureInfo.CurrentUICulture.Name;
                if (cultureName.StartsWith("es", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentLanguage = "es";
                }
            }
            catch { }
        }

        public static bool IsSpanish => CurrentLanguage == "es";

        // Dictionary of translations
        private static readonly Dictionary<string, Dictionary<string, string>> Translations = new()
        {
            ["es"] = new()
            {
                ["ToggleSidebar"] = "Mostrar/Ocultar índice",
                ["OpenPdfFile"] = "Abrir archivo PDF",
                ["Open"] = "Abrir",
                ["PrevPage"] = "Página anterior",
                ["NextPage"] = "Página siguiente",
                ["ReadMode"] = "Modo Lectura / Navegar",
                ["HighlightMode"] = "Modo Marca-textos / Resaltar",
                ["NoteMode"] = "Modo Notas / Comentarios",
                ["PenMode"] = "Modo Lápiz / Escritura libre",
                ["EraserMode"] = "Modo Borrador",
                ["ColorYellow"] = "Color Amarillo",
                ["ColorGreen"] = "Color Verde",
                ["ColorCyan"] = "Color Celeste",
                ["ColorMagenta"] = "Color Rosa",
                ["ColorRed"] = "Color Rojo",
                ["ColorBlue"] = "Color Azul",
                ["ColorBlack"] = "Color Negro",
                ["Thickness"] = "Grosor:",
                ["Opacity"] = "Opacidad:",
                ["Rotate"] = "Rotar vista (90°)",
                ["SplitView"] = "Vista Dividida (Comparar PDFs)",
                ["SaveOriginal"] = "Guardar en el archivo original",
                ["Settings"] = "Configuración",
                ["AppTheme"] = "Tema de la aplicación",
                ["ThemeLight"] = "Claro",
                ["ThemeDark"] = "Oscuro",
                ["ThemeSystem"] = "Predeterminado del sistema",
                ["AutoSaveHeader"] = "Guardado automático",
                ["AutoSaveSubtext"] = "Guardar marcas automáticamente al salir",
                ["AboutTitle"] = "Acerca de Glance",
                ["Developer"] = "Desarrollado por jonas1ara",
                ["WebsiteLink"] = "Sitio Web Oficial",
                ["GitHubLink"] = "Repositorio de GitHub",
                ["WelcomeSubtitle"] = "Abre un documento para comenzar o selecciona uno de tus archivos recientes.",
                ["WelcomeOpenButton"] = "Abrir Documento PDF",
                ["RecentDocuments"] = "Documentos Recientes",
                ["Thumbnails"] = "Miniaturas",
                ["OpenSecondPdf"] = "Abrir segundo PDF",
                ["OpenSecondPdfText"] = "Abrir PDF para Comparación",
                ["SideBySideComparer"] = "Comparador Lado a Lado",
                ["CloseComparison"] = "Cerrar Comparación",
                ["SearchPlaceholder"] = "Buscar texto...",
                ["SearchPrev"] = "Anterior",
                ["SearchNext"] = "Siguiente",
                ["SearchClose"] = "Cerrar (Esc)",
                ["SearchStatus"] = "{0} de {1}",
                ["UnsavedChangesTitle"] = "Cambios sin guardar",
                ["UnsavedChangesContent"] = "¿Deseas guardar los cambios en el archivo PDF antes de salir?",
                ["SaveAndExit"] = "Guardar y Salir",
                ["ExitWithoutSaving"] = "Salir sin guardar",
                ["Cancel"] = "Cancelar",
                ["Home"] = "Inicio",
                ["HomeTooltip"] = "Inicio / Archivos recientes",
                ["ViewModeContinuous"] = "Continuo",
                ["ViewModeSingle"] = "Página individual",
                ["ViewModeTwoPage"] = "Dos páginas",
                ["FullScreen"] = "Pantalla completa (F11)",
                ["Page"] = "Página",
                ["SaveCopy"] = "Guardar Copia",
                ["DropToOpen"] = "Soltar para abrir",
                ["ZoomAnchor"] = "El zoom se centra en",
                ["ZoomAnchorCursor"] = "El cursor",
                ["ZoomAnchorCenter"] = "El centro de la página"
            },
            ["en"] = new()
            {
                ["ToggleSidebar"] = "Show/Hide index",
                ["OpenPdfFile"] = "Open PDF file",
                ["Open"] = "Open",
                ["PrevPage"] = "Previous page",
                ["NextPage"] = "Next page",
                ["ReadMode"] = "Read Mode / Navigate",
                ["HighlightMode"] = "Highlight Mode",
                ["NoteMode"] = "Notes Mode",
                ["PenMode"] = "Pen Mode / Freehand drawing",
                ["EraserMode"] = "Eraser Mode",
                ["ColorYellow"] = "Yellow Color",
                ["ColorGreen"] = "Green Color",
                ["ColorCyan"] = "Cyan Color",
                ["ColorMagenta"] = "Magenta Color",
                ["ColorRed"] = "Red Color",
                ["ColorBlue"] = "Blue Color",
                ["ColorBlack"] = "Black Color",
                ["Thickness"] = "Thickness:",
                ["Opacity"] = "Opacity:",
                ["Rotate"] = "Rotate view (90°)",
                ["SplitView"] = "Split View (Compare PDFs)",
                ["SaveOriginal"] = "Save to original file",
                ["Settings"] = "Settings",
                ["AppTheme"] = "App Theme",
                ["ThemeLight"] = "Light",
                ["ThemeDark"] = "Dark",
                ["ThemeSystem"] = "System default",
                ["AutoSaveHeader"] = "Auto-save",
                ["AutoSaveSubtext"] = "Save changes automatically on exit",
                ["AboutTitle"] = "About Glance",
                ["Developer"] = "Developed by jonas1ara",
                ["WebsiteLink"] = "Official Website",
                ["GitHubLink"] = "GitHub Repository",
                ["WelcomeSubtitle"] = "Open a document to get started or select one of your recent files.",
                ["WelcomeOpenButton"] = "Open PDF Document",
                ["RecentDocuments"] = "Recent Documents",
                ["Thumbnails"] = "Thumbnails",
                ["OpenSecondPdf"] = "Open second PDF",
                ["OpenSecondPdfText"] = "Open PDF for Comparison",
                ["SideBySideComparer"] = "Side-by-Side Comparer",
                ["CloseComparison"] = "Close Comparison",
                ["SearchPlaceholder"] = "Search text...",
                ["SearchPrev"] = "Previous",
                ["SearchNext"] = "Next",
                ["SearchClose"] = "Close (Esc)",
                ["SearchStatus"] = "{0} of {1}",
                ["UnsavedChangesTitle"] = "Unsaved Changes",
                ["UnsavedChangesContent"] = "Do you want to save the changes to the PDF file before exiting?",
                ["SaveAndExit"] = "Save and Exit",
                ["ExitWithoutSaving"] = "Exit without saving",
                ["Cancel"] = "Cancel",
                ["Home"] = "Home",
                ["HomeTooltip"] = "Home / Recent files",
                ["ViewModeContinuous"] = "Continuous",
                ["ViewModeSingle"] = "Single page",
                ["ViewModeTwoPage"] = "Two pages",
                ["FullScreen"] = "Full screen (F11)",
                ["Page"] = "Page",
                ["SaveCopy"] = "Save Copy",
                ["DropToOpen"] = "Drop to open",
                ["ZoomAnchor"] = "Zoom centers on",
                ["ZoomAnchorCursor"] = "The cursor",
                ["ZoomAnchorCenter"] = "The page center"
            }
        };

        public static string Get(string key)
        {
            string lang = IsSpanish ? "es" : "en";
            if (Translations[lang].TryGetValue(key, out string? val) && val != null)
            {
                return val;
            }
            // Fallback to English if key not found
            if (Translations["en"].TryGetValue(key, out string? fallbackVal) && fallbackVal != null)
            {
                return fallbackVal;
            }
            return key;
        }
    }
}
