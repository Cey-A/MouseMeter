using System;

namespace MouseMeter
{
    /// <summary>
    /// Pure decisions behind the widget's material (blur) layer. Kept apart from <see cref="HudForm"/>
    /// on purpose: everything here can be asserted in the test runner, which loads without a
    /// message loop and would otherwise drag WinForms initialisation into a console test.
    /// </summary>
    public static class Material
    {
        /// <summary>Solid card: no blur requested.</summary>
        public const int Solid = 0;

        /// <summary>Blur via SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND).</summary>
        public const int Accent = 1;

        /// <summary>Blur via DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE).</summary>
        public const int Backdrop = 2;

        /// <summary>
        /// Which layer to ask for. The order of the checks is the fallback chain, and each
        /// guard exists for a concrete failure we have seen:
        ///
        ///   100%             the user asked for a plain card; honour it instead of second-guessing
        ///   transparency off DWM ignores the accent policy, leaving an unreadable slab
        ///   no acrylic       Windows 10 1709 and older has neither API
        ///   backdrop         Win11 22H2+; older Win10 keeps working through the accent policy
        /// </summary>
        public static int Pick(int hudOpacity, bool transparencyOn, bool acrylicOk, bool backdropOk)
        {
            if (hudOpacity >= 100) return Solid;
            if (!transparencyOn) return Solid;
            if (!acrylicOk) return Solid;
            return backdropOk ? Backdrop : Accent;
        }

        /// <summary>
        /// Slider (55..99) to tint alpha. The low end has to stay clearly see-through — that is
        /// the whole point of the material — while the high end still has to read as a card
        /// rather than a hole in the desktop, hence the 130..228 window instead of 0..255.
        /// </summary>
        public static int TintAlpha(int hudOpacity, bool dark)
        {
            int pct = Math.Max(55, Math.Min(99, hudOpacity));
            int a = (int)Math.Round(130 + (pct - 55) * (228 - 130) / 44.0);
            // Light text on a bright wallpaper is the failure mode called out in the roadmap;
            // a thin dark tint simply disappears against busy content, so give it extra body.
            if (dark) a = Math.Min(255, a + 22);
            return a;
        }

        /// <summary>DWM tint word, 0xAABBGGRR. Swapping R and B here is invisible in review.</summary>
        public static int TintWord(int alpha, int r, int g, int b)
        {
            return ((alpha & 0xFF) << 24) | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
        }
    }
}
