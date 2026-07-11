using System;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using xPasteWin.ViewModels;

namespace xPasteWin.Views;

public sealed partial class ClipboardItemCard : UserControl
{
    public ClipboardItemCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SetupIconShadow();
    }

    /// <summary>
    /// Dựng drop shadow bám theo hình icon app nguồn (mask = alpha của ảnh icon), thay vì
    /// ThemeShadow đổ bóng theo khung vuông. Chạy lại mỗi khi card được tái sử dụng cho item khác.
    /// </summary>
    private void SetupIconShadow()
    {
        var path = (DataContext as CardViewModel)?.SourceAppIconPath;
        if (string.IsNullOrEmpty(path))
        {
            ElementCompositionPreview.SetElementChildVisual(IconShadowHost, null);
            return;
        }
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(IconShadowHost).Compositor;
            var surface = LoadedImageSurface.StartLoadFromUri(new Uri(path));
            var mask = compositor.CreateSurfaceBrush(surface);
            mask.Stretch = CompositionStretch.Uniform;

            var shadow = compositor.CreateDropShadow();
            shadow.Mask = mask;                 // bóng theo alpha của icon
            shadow.BlurRadius = 7;
            shadow.Opacity = 0.55f;
            shadow.Color = Colors.Black;
            shadow.Offset = new Vector3(0, 2, 0);

            var sprite = compositor.CreateSpriteVisual();
            sprite.Size = new Vector2(36, 36);
            sprite.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(IconShadowHost, sprite);
        }
        catch
        {
            ElementCompositionPreview.SetElementChildVisual(IconShadowHost, null);
        }
    }
}
