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

        // ---------------------------------------------------------------- surface rules
        //
        // The four methods below answer "given this material, how must the *window* be set up".
        // They live here rather than inside HudForm for the same reason Pick does: the bug that
        // made the blur invisible was not a wrong decision about which layer to use, it was the
        // surface being configured inconsistently after the decision. A rule that can only be
        // checked by looking at a window cannot be checked at all without a display.

        /// <summary>
        /// The value to assign to <c>Form.Opacity</c>. Blur layers must run at exactly 1.0:
        /// anything else makes WinForms set WS_EX_LAYERED, and a layered window is composited
        /// above the DWM material instead of letting it through — the card goes flat.
        /// </summary>
        public static double OpacityFor(int material, int hudOpacity)
        {
            if (material != Solid) return 1.0;
            return Math.Max(0.55, Math.Min(1.0, hudOpacity / 100.0));
        }

        /// <summary>
        /// True when the form's BackColor must be <c>Color.Transparent</c>. On the blur paths a
        /// real colour makes WinForms fill the whole client area, covering the material.
        /// </summary>
        public static bool BackColorIsTransparent(int material)
        {
            return material != Solid;
        }

        /// <summary>
        /// True when Opacity may be used (and with it WS_EX_LAYERED). Only the solid path may:
        /// on 22621+ the layered style and DWMWA_SYSTEMBACKDROP_TYPE are mutually destructive.
        /// </summary>
        public static bool LayeredAllowed(int material)
        {
            return material == Solid;
        }

        /// <summary>
        /// True when OnPaint has to clear to Transparent before laying down the tint. Without
        /// it the pixels outside the rounded path keep the previous frame and the card reads as
        /// opaque regardless of tint alpha.
        /// </summary>
        public static bool ClearsBeforeTint(int material)
        {
            return material != Solid;
        }

        /// <summary>
        /// The GDI half of the tint: <c>TintAlpha * 0.55</c>. Split from DWM deliberately — all
        /// of it painted here would hide the blur, none of it would leave white text on a bright
        /// wallpaper. Kept as a function so the ratio is asserted rather than eyeballed.
        /// </summary>
        public static int GdiTintAlpha(int hudOpacity, bool dark)
        {
            return (int)Math.Round(TintAlpha(hudOpacity, dark) * 0.55);
        }
    }
}
