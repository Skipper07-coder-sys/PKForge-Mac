using System.Runtime.CompilerServices;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using PKForge.App.Theme;
using UIKit;

namespace PKForge.App.Views;

/// <summary>
/// The Mac build of the capsule skin: the same gradient body, 1 pt top light, edge, hard drop and
/// accent strip as Android. MAUI builds buttons from a UIButtonConfiguration, so the skin is that
/// configuration's background view. Every button also opts out of the native Mac push-button look,
/// which would otherwise ignore MAUI's colors.
/// </summary>
public static partial class CapsuleSkin
{
    private static bool _registered;

    /// <summary>The drawn capsule. Lays its layers out from the last applied look whenever it resizes.</summary>
    private sealed class SkinView : UIView
    {
        private readonly CALayer _shadow = new();
        private readonly CAGradientLayer _body = new();
        private readonly CALayer _light = new();
        private readonly CALayer _strip = new();

        public Color Body = UiTokens.ButtonTop;
        public Color Edge = UiTokens.ButtonEdge;
        public Color Strip = Colors.Transparent;
        public bool Enabled = true, Primary, Pressed;
        public nfloat Radius = (nfloat)UiTokens.ControlRadius, Stroke = 1, DropDepth = (nfloat)Drop;

        public SkinView()
        {
            UserInteractionEnabled = false;
            BackgroundColor = UIColor.Clear;
            foreach (var layer in new[] { _shadow, _body, _light, _strip })
                Layer.AddSublayer(layer);
            _strip.MaskedCorners = CACornerMask.MinXMinYCorner | CACornerMask.MinXMaxYCorner;
        }

        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            // No implicit animations: the skin must track presses and resizes instantly.
            CATransaction.Begin();
            CATransaction.DisableActions = true;
            var bounds = Bounds;
            var sink = Pressed ? DropDepth : 0;
            var bodyHeight = (nfloat)Math.Max(0, bounds.Height - DropDepth);

            _shadow.Frame = new CGRect(0, DropDepth, bounds.Width, bodyHeight);
            _shadow.CornerRadius = Radius;
            _shadow.BackgroundColor = UiTokens.PanelShadow.WithAlpha(Enabled ? 0.9f : 0.45f).ToCGColor();

            var top = Pressed ? Shade(Body, -0.02f) : Shade(Body, Enabled ? 0.16f : 0.03f);
            var bottom = Pressed ? Shade(Body, -0.12f) : Shade(Body, -0.14f);
            _body.Frame = new CGRect(0, sink, bounds.Width, bodyHeight);
            _body.Colors = [top.ToCGColor(), bottom.ToCGColor()];
            _body.CornerRadius = Radius;
            _body.BorderWidth = Stroke;
            _body.BorderColor = Edge.ToCGColor();

            var inset = Stroke + Radius / 2;
            _light.Frame = new CGRect(inset, sink + Stroke, Math.Max(0, bounds.Width - 2 * inset), 1);
            _light.BackgroundColor = Colors.White.WithAlpha(Pressed || !Enabled ? 0.06f : Primary ? 0.34f : 0.22f).ToCGColor();

            _strip.Frame = new CGRect(Stroke, sink + Stroke, 3, Math.Max(0, bodyHeight - 2 * Stroke));
            _strip.CornerRadius = (nfloat)Math.Max(0, Radius - Stroke);
            _strip.BackgroundColor = Strip.ToCGColor();
            CATransaction.Commit();
        }
    }

    private static readonly ConditionalWeakTable<UIButton, SkinView> Skins = new();
    private static readonly Dictionary<string, UIImage> Icons = new(StringComparer.Ordinal);

    /// <summary>Hooks the Button handler once (called from MauiProgram).</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        // Mac idiom draws every UIButton as a native push button and ignores custom fills.
        ButtonHandler.Mapper.AppendToMapping("PKForgeBehavior", (h, _) =>
        {
            if (h.PlatformView is UIButton native && native.PreferredBehavioralStyle != UIBehavioralStyle.Pad)
                native.PreferredBehavioralStyle = UIBehavioralStyle.Pad;
        });
        // Configuration-based buttons on Mac never receive MAUI's font (every title fell back to
        // the system font): hand the resolved font to the configuration's title attributes.
        ButtonHandler.Mapper.AppendToMapping(nameof(ITextStyle.Font), (h, v) => ApplyFont(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(IText.Text), (h, v) => ApplyFont(h, v));
        // MAUI rebuilds the configuration in each of these mappings; re-skin after every one.
        ButtonHandler.Mapper.AppendToMapping(nameof(IButton.Background), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(IButtonStroke.StrokeColor), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(IButtonStroke.StrokeThickness), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(IButtonStroke.CornerRadius), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(IView.IsEnabled), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(ITextStyle.TextColor), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(ITextStyle.Font), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(IText.Text), (h, v) => Apply(h, v));
        ButtonHandler.Mapper.AppendToMapping(nameof(IPadding.Padding), (h, v) => Apply(h, v));
    }

    private static void Apply(IButtonHandler handler, IButton view)
    {
        if (view is not Button button || !States.TryGetValue(button, out var state)) return;
        if (handler.PlatformView is not UIButton native || native.Configuration is not { } config) return;

        var skin = Skins.GetValue(native, n =>
        {
            var created = new SkinView();
            void Press(bool pressed)
            {
                if (created.Pressed == pressed) return;
                created.Pressed = pressed;
                created.SetNeedsLayout();
            }
            n.TouchDown += (_, _) => Press(true);
            n.TouchDragEnter += (_, _) => Press(true);
            n.TouchDragExit += (_, _) => Press(false);
            n.TouchUpInside += (_, _) => Press(false);
            n.TouchUpOutside += (_, _) => Press(false);
            n.TouchCancel += (_, _) => Press(false);
            return created;
        });

        var enabled = button.IsEnabled;
        var body = button.BackgroundColor ?? UiTokens.ButtonTop;
        var edge = button.BorderColor ?? UiTokens.ButtonEdge;
        if (!enabled)
        {
            body = Color.FromRgba((body.Red + Grey(body).Red) / 2 * 0.8f, (body.Green + Grey(body).Green) / 2 * 0.8f, (body.Blue + Grey(body).Blue) / 2 * 0.8f, 1f);
            edge = UiTokens.ButtonEdge.WithAlpha(0.5f);
        }
        skin.Body = body;
        skin.Edge = edge;
        skin.Enabled = enabled;
        skin.Primary = state.Primary;
        skin.Radius = (nfloat)(button.CornerRadius >= 0 ? button.CornerRadius : UiTokens.ControlRadius);
        skin.Stroke = (nfloat)Math.Max(1, button.BorderWidth > 0 ? button.BorderWidth : UiTokens.ControlEdge);
        skin.DropDepth = (nfloat)(Drop + (state.Focused ? 1 : 0));
        skin.Strip = state.Strip is { } sc && !state.Focused ? (enabled ? sc : sc.WithAlpha(0.35f)) : Colors.Transparent;
        skin.SetNeedsLayout();

        // The skin is the whole background; MAUI's fill, stroke and radius go.
        native.BackgroundColor = UIColor.Clear;
        native.Layer.BorderWidth = 0;
        var background = UIBackgroundConfiguration.ClearConfiguration;
        background.CustomView = skin;
        config.Background = background;

        var ink = button.TextColor ?? UiTokens.Ink0;
        config.BaseForegroundColor = (enabled ? ink : ink.WithAlpha(0.45f)).ToPlatform();
        if (state.Icon is { } icon && IconImage(icon, button, state) is { } image)
        {
            config.Image = image;
            config.ImagePadding = 6;
            config.ImagePlacement = NSDirectionalRectEdge.Leading;
        }
        // Text sits centered in the body, not the body + shadow box.
        var p = button.Padding;
        config.ContentInsets = new NSDirectionalEdgeInsets((nfloat)p.Top, (nfloat)p.Left, (nfloat)p.Bottom + skin.DropDepth, (nfloat)p.Right);
        native.Configuration = config;
    }

    private static void ApplyFont(IButtonHandler handler, IButton view)
    {
        if (handler.PlatformView is not UIButton native || native.Configuration is not { } config) return;
        if (view is not ITextStyle text || handler.MauiContext?.Services.GetService<IFontManager>() is not { } fonts) return;
        var font = fonts.GetFont(text.Font);
        config.TitleTextAttributesTransformer = attributes =>
        {
            var copy = new NSMutableDictionary<NSString, NSObject>();
            foreach (var (key, value) in attributes) copy[key] = value;
            copy[UIStringAttributeKey.Font] = font;
            return copy;
        };
        native.Configuration = config;
    }

    private static UIImage? IconImage(string icon, Button button, State state)
    {
        try
        {
            var neutral = button.BackgroundColor is null || Near(button.BackgroundColor, UiTokens.ButtonTop);
            var tint = neutral && !state.Focused ? PksmIcons.Cyan : PksmIcons.White;
            var size = Math.Round(button.FontSize + 3);
            var key = $"{icon}|{tint}|{size}|{button.IsEnabled}";
            if (Icons.TryGetValue(key, out var cached)) return cached;
            var png = PksmIcons.GetPng(icon, tint);
            using var raw = UIImage.LoadFromData(NSData.FromArray(png));
            if (raw is null) return null;
            // Pixel art: scale with nearest-neighbour so the icon stays crisp.
            var renderer = new UIGraphicsImageRenderer(new CGSize(size, size));
            var image = renderer.CreateImage(context =>
            {
                context.CGContext.InterpolationQuality = CGInterpolationQuality.None;
                raw.Draw(new CGRect(0, 0, size, size), CGBlendMode.Normal, button.IsEnabled ? 1f : 0.43f);
            }).ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
            Icons[key] = image;
            return image;
        }
        catch
        {
            return null; // icon is decoration; never fail the button
        }
    }
}
