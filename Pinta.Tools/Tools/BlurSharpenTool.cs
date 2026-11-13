//
// BlurSharpenTool.cs
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using Cairo;
using Gtk;
using Pinta.Core;

namespace Pinta.Tools;

/// <summary>
/// A brush that locally blurs or sharpens the current layer.
/// The filtered result is always computed from the layer as it was when the stroke started
/// (see <see cref="BaseBrushTool.undo_surface"/>), and the strongest brush coverage reached by each pixel
/// is remembered for the duration of the stroke. This makes the result of a stroke independent of
/// how fast the mouse moves, and of how many times the brush passes over the same pixel.
/// </summary>
public sealed class BlurSharpenTool : BaseBrushTool
{
	private enum BlurSharpenMode
	{
		Blur = 0,
		Sharpen = 1,
	}

	/// <summary>
	/// The brush width used until the user picks one. Pinta's default width of a couple of pixels
	/// is too small for blurring or sharpening to be noticeable.
	/// </summary>
	private const int DEFAULT_BLUR_SHARPEN_BRUSH_WIDTH = 20;

	/// <summary>
	/// The smallest and largest radius (in pixels) of the blur kernel. The radius used for a stroke grows with the brush width.
	/// </summary>
	private const int MIN_KERNEL_RADIUS = 2;
	private const int MAX_KERNEL_RADIUS = 20;

	/// <summary>
	/// The fraction of the brush radius, measured from the center, which is affected with full strength.
	/// Beyond this the effect fades smoothly to nothing at the edge of the brush.
	/// </summary>
	private const double FULL_STRENGTH_FRACTION = 0.5;

	/// <summary>
	/// How strongly the difference between a pixel and its blurred version is amplified when sharpening.
	/// </summary>
	private const float SHARPEN_GAIN = 2f;

	private const int FALLOFF_RESOLUTION = 256;

	private static readonly byte[] falloff_table = CreateFalloffTable ();

	private readonly IWorkspaceService workspace;

	private BlurSharpenMode mode = BlurSharpenMode.Blur;

	private PointI? last_point = null;

	// The strongest coverage (0-255) that the brush has reached for each pixel of the image during the current stroke.
	private byte[]? coverage;

	// Scratch buffers, kept around between mouse events to avoid reallocating them.
	private float[] horizontal_pass = [];
	private float[] blurred_pixels = [];

	private int kernel_radius = 0;
	private float[] kernel = [];

	public BlurSharpenTool (IServiceProvider services) : base (services)
	{
		workspace = services.GetService<IWorkspaceService> ();

		// Use a larger default brush width, unless the user has already chosen one.
		if (Settings.GetSetting (SettingNames.BrushWidth (this), -1) < 0)
			BrushWidth = DEFAULT_BLUR_SHARPEN_BRUSH_WIDTH;

		// Update cursor on zoom
		workspace.ViewSizeChanged += (_, _) => {
			if (IsActiveTool ()) {
				SetCursor (DefaultCursor);
			}
		};
	}

	public override string Name
		=> Translations.GetString ("Blur/Sharpen");

	public override string Icon
		=> Pinta.Resources.Icons.ToolBlurSharpen;

	public override string StatusBarText
		=> Translations.GetString ("Left click to blur or sharpen, depending on the selected mode.");

	public override Gdk.Key ShortcutKey
		=> new (Gdk.Constants.KEY_U);

	public override int Priority => 51;

	// The brush works directly on the pixels, so antialiasing does not apply.
	protected override bool ShowAntialiasingButton => false;

	public override Gdk.Cursor DefaultCursor {
		get {
			double scale = workspace.GetScale ();
			var icon = GdkExtensions.CreateIconWithShape (
				"Cursor.Paintbrush.png",
				CursorShape.Ellipse,
				scale,
				BrushWidth,
				8,
				24,
				out int iconOffsetX,
				out int iconOffsetY);

			return Gdk.Cursor.NewFromTexture (icon, iconOffsetX, iconOffsetY, null);
		}
	}

	private float Strength => (float) (StrengthSlider.GetValue () / 100);

	protected override void OnBuildToolBar (Box tb)
	{
		base.OnBuildToolBar (tb);

		tb.Append (Separator);

		tb.Append (ModeLabel);
		tb.Append (ModeComboBox);

		tb.Append (StrengthLabel);
		tb.Append (StrengthSlider);
	}

	protected override void OnMouseDown (Document document, ToolMouseEventArgs e)
	{
		// The base class ignores additional mouse down events while drawing, so we must as well.
		if (mouse_button != MouseButton.None)
			return;

		coverage = new byte[document.ImageSize.Width * document.ImageSize.Height];
		last_point = null;

		base.OnMouseDown (document, e);
	}

	protected override void OnMouseMove (Document document, ToolMouseEventArgs e)
	{
		// Both of these are set up when the stroke begins.
		if (undo_surface is null || coverage is null)
			return;

		if (mouse_button != MouseButton.Left) {
			last_point = null;
			return;
		}

		PointI start = last_point ?? e.Point;
		PointI end = e.Point;
		last_point = end;

		if (document.Workspace.PointInCanvas (e.PointDouble))
			surface_modified = true;

		int width = undo_surface.Width;
		int height = undo_surface.Height;

		// Pixels that are within this distance of the stroke segment are affected by the brush.
		double radius = Math.Max (BrushWidth / 2.0, 0.5);
		double outer_radius = radius + 0.5;
		int padding = (int) Math.Ceiling (outer_radius);

		// The region of pixels which may be modified by this segment (right and bottom edges are exclusive).
		int left = Math.Max (Math.Min (start.X, end.X) - padding, 0);
		int top = Math.Max (Math.Min (start.Y, end.Y) - padding, 0);
		int right = Math.Min (Math.Max (start.X, end.X) + padding + 1, width);
		int bottom = Math.Min (Math.Max (start.Y, end.Y) + padding + 1, height);

		if (left >= right || top >= bottom)
			return;

		if (!UpdateCoverage (coverage, width, left, top, right, bottom, start, end, outer_radius))
			return;

		// Make sure the pixel data of the stroke's source image is up to date before reading it.
		undo_surface.Flush ();

		int new_kernel_radius = Math.Clamp (BrushWidth / 4, MIN_KERNEL_RADIUS, MAX_KERNEL_RADIUS);
		if (new_kernel_radius != kernel_radius || kernel.Length == 0) {
			kernel_radius = new_kernel_radius;
			kernel = CreateKernel (kernel_radius);
		}

		ReadOnlySpan<ColorBgra> source_data = undo_surface.GetReadOnlyPixelData ();

		ComputeBlur (source_data, width, height, left, top, right, bottom);

		int region_width = right - left;
		int region_height = bottom - top;

		using ImageSurface result = CairoExtensions.CreateImageSurface (Format.Argb32, region_width, region_height);
		result.Flush ();
		Span<ColorBgra> result_data = result.GetPixelData ();

		float strength = Strength;
		BlurSharpenMode current_mode = mode;

		for (int y = top; y < bottom; y++) {
			for (int x = left; x < right; x++) {

				int index = y * width + x;
				ColorBgra original = source_data[index];
				byte pixel_coverage = coverage[index];

				result_data[(y - top) * region_width + (x - left)] =
					pixel_coverage == 0
					? original
					: ApplyFilter (
						current_mode,
						original,
						strength * pixel_coverage / 255f,
						blurred_pixels.AsSpan (((y - top) * region_width + (x - left)) * 4, 4));
			}
		}

		result.MarkDirty ();

		// Paint the result through the selection clip, replacing (not blending with) the existing pixels.
		using Context g = document.CreateClippedContext ();
		g.Operator = Operator.Source;
		g.SetSourceSurface (result, left, top);
		g.Rectangle (new RectangleD (left, top, region_width, region_height));
		g.Fill ();

		document.Workspace.Invalidate (new RectangleI (left, top, region_width, region_height));
	}

	protected override void OnMouseUp (Document document, ToolMouseEventArgs e)
	{
		// The base class adds the history item, using the surface that the stroke was computed from.
		base.OnMouseUp (document, e);

		coverage = null;
		last_point = null;
	}

	protected override void OnSaveSettings (ISettingsService settings)
	{
		base.OnSaveSettings (settings);

		if (mode_combobox is not null)
			settings.PutSetting (SettingNames.BLUR_SHARPEN_MODE, mode_combobox.ComboBox.Active);

		if (strength_slider is not null)
			settings.PutSetting (SettingNames.BLUR_SHARPEN_STRENGTH, (int) strength_slider.GetValue ());
	}

	/// <summary>
	/// Raises the stored coverage of the pixels in the given region to the coverage that the brush has
	/// when it is dragged from <paramref name="start"/> to <paramref name="end"/>.
	/// </summary>
	/// <returns>True if the coverage of any pixel was increased.</returns>
	private static bool UpdateCoverage (
		byte[] coverage,
		int image_width,
		int left,
		int top,
		int right,
		int bottom,
		PointI start,
		PointI end,
		double outer_radius)
	{
		double segment_x = end.X - start.X;
		double segment_y = end.Y - start.Y;
		double segment_length_squared = segment_x * segment_x + segment_y * segment_y;

		bool changed = false;

		for (int y = top; y < bottom; y++) {
			for (int x = left; x < right; x++) {

				// Find the distance from this pixel to the closest point on the segment.
				double t = 0;
				if (segment_length_squared > 0) {
					t = ((x - start.X) * segment_x + (y - start.Y) * segment_y) / segment_length_squared;
					t = Math.Clamp (t, 0, 1);
				}

				double dx = x - (start.X + t * segment_x);
				double dy = y - (start.Y + t * segment_y);
				double distance = Math.Sqrt (dx * dx + dy * dy);

				if (distance >= outer_radius)
					continue;

				byte pixel_coverage = falloff_table[(int) (distance / outer_radius * (FALLOFF_RESOLUTION - 1))];

				int index = y * image_width + x;
				if (pixel_coverage > coverage[index]) {
					coverage[index] = pixel_coverage;
					changed = true;
				}
			}
		}

		return changed;
	}

	/// <summary>
	/// Computes a blurred copy of the given region (right and bottom edges are exclusive) of the source image.
	/// The result is stored in <see cref="blurred_pixels"/>, as four floats (B, G, R, A) per pixel of the region.
	/// The pixel values are premultiplied by alpha, and pixels beyond the edge of the image are treated as
	/// having the same color as the closest pixel inside the image.
	/// </summary>
	private void ComputeBlur (
		ReadOnlySpan<ColorBgra> source,
		int width,
		int height,
		int left,
		int top,
		int right,
		int bottom)
	{
		int region_width = right - left;
		int region_height = bottom - top;

		// Rows of the source that the vertical pass needs. These are clamped to the image,
		// and every clamped row that the vertical pass asks for is within this range.
		int source_top = Math.Max (top - kernel_radius, 0);
		int source_bottom = Math.Min (bottom + kernel_radius, height);
		int source_rows = source_bottom - source_top;

		int horizontal_length = source_rows * region_width * 4;
		if (horizontal_pass.Length < horizontal_length)
			horizontal_pass = new float[horizontal_length];

		int blurred_length = region_height * region_width * 4;
		if (blurred_pixels.Length < blurred_length)
			blurred_pixels = new float[blurred_length];

		// Horizontal pass
		for (int y = source_top; y < source_bottom; y++) {
			ReadOnlySpan<ColorBgra> row = source.Slice (y * width, width);

			for (int x = left; x < right; x++) {

				float b = 0, g = 0, r = 0, a = 0;

				for (int k = -kernel_radius; k <= kernel_radius; k++) {
					ColorBgra pixel = row[Math.Clamp (x + k, 0, width - 1)];
					float weight = kernel[k + kernel_radius];
					b += weight * pixel.B;
					g += weight * pixel.G;
					r += weight * pixel.R;
					a += weight * pixel.A;
				}

				int offset = ((y - source_top) * region_width + (x - left)) * 4;
				horizontal_pass[offset] = b;
				horizontal_pass[offset + 1] = g;
				horizontal_pass[offset + 2] = r;
				horizontal_pass[offset + 3] = a;
			}
		}

		// Vertical pass
		for (int y = top; y < bottom; y++) {
			for (int x = left; x < right; x++) {

				float b = 0, g = 0, r = 0, a = 0;

				for (int k = -kernel_radius; k <= kernel_radius; k++) {
					int source_row = Math.Clamp (y + k, 0, height - 1) - source_top;
					int offset = (source_row * region_width + (x - left)) * 4;
					float weight = kernel[k + kernel_radius];
					b += weight * horizontal_pass[offset];
					g += weight * horizontal_pass[offset + 1];
					r += weight * horizontal_pass[offset + 2];
					a += weight * horizontal_pass[offset + 3];
				}

				int result_offset = ((y - top) * region_width + (x - left)) * 4;
				blurred_pixels[result_offset] = b;
				blurred_pixels[result_offset + 1] = g;
				blurred_pixels[result_offset + 2] = r;
				blurred_pixels[result_offset + 3] = a;
			}
		}
	}

	/// <param name="original">The pixel from the start of the stroke (premultiplied by alpha).</param>
	/// <param name="amount">How strongly to apply the filter, from 0 to 1.</param>
	/// <param name="blurred">The blurred pixel as B, G, R, A (premultiplied by alpha).</param>
	private static ColorBgra ApplyFilter (BlurSharpenMode mode, ColorBgra original, float amount, ReadOnlySpan<float> blurred)
	{
		switch (mode) {

			case BlurSharpenMode.Sharpen:
				// Unsharp mask. Alpha is left alone, and the color channels must not exceed alpha
				// since the pixel is premultiplied.
				float max = original.A;
				return ColorBgra.FromBgra (
					b: SharpenChannel (original.B, blurred[0], amount, max),
					g: SharpenChannel (original.G, blurred[1], amount, max),
					r: SharpenChannel (original.R, blurred[2], amount, max),
					a: original.A);

			case BlurSharpenMode.Blur:
			default:
				// Blending two premultiplied pixels always results in a valid premultiplied pixel.
				return ColorBgra.FromBgra (
					b: Utility.ClampToByte (original.B + amount * (blurred[0] - original.B) + 0.5f),
					g: Utility.ClampToByte (original.G + amount * (blurred[1] - original.G) + 0.5f),
					r: Utility.ClampToByte (original.R + amount * (blurred[2] - original.R) + 0.5f),
					a: Utility.ClampToByte (original.A + amount * (blurred[3] - original.A) + 0.5f));
		}
	}

	private static byte SharpenChannel (byte original, float blurred, float amount, float max)
		=> Utility.ClampToByte (Math.Clamp (original + amount * SHARPEN_GAIN * (original - blurred), 0f, max) + 0.5f);

	/// <summary>
	/// Creates a normalized Gaussian kernel of size (2 * radius + 1).
	/// </summary>
	private static float[] CreateKernel (int radius)
	{
		double sigma = Math.Max (radius / 2.0, 0.5);

		float[] weights = new float[2 * radius + 1];
		double sum = 0;

		for (int i = 0; i < weights.Length; i++) {
			double distance = i - radius;
			double weight = Math.Exp (-(distance * distance) / (2 * sigma * sigma));
			weights[i] = (float) weight;
			sum += weight;
		}

		for (int i = 0; i < weights.Length; i++)
			weights[i] = (float) (weights[i] / sum);

		return weights;
	}

	/// <summary>
	/// Creates a table of the brush coverage (0-255), indexed by the distance from the center of the
	/// brush as a fraction of the brush radius. The brush is fully opaque in its center (see <see cref="FULL_STRENGTH_FRACTION"/>)
	/// and then fades smoothly to nothing at the edge.
	/// </summary>
	private static byte[] CreateFalloffTable ()
	{
		byte[] table = new byte[FALLOFF_RESOLUTION];

		for (int i = 0; i < table.Length; i++) {
			double fraction = (double) i / (FALLOFF_RESOLUTION - 1);

			if (fraction <= FULL_STRENGTH_FRACTION) {
				table[i] = 255;
				continue;
			}

			// Fade out over the remaining distance to the edge of the brush.
			double fade = (fraction - FULL_STRENGTH_FRACTION) / (1 - FULL_STRENGTH_FRACTION);
			table[i] = (byte) Math.Round (255 * 0.5 * (1 + Math.Cos (Math.PI * fade)));
		}

		return table;
	}

	private Label? mode_label;
	private ToolBarComboBox? mode_combobox;
	private Label? strength_label;
	private Scale? strength_slider;
	private Separator? separator;

	private Label ModeLabel => mode_label ??= Label.New (string.Format (" {0}: ", Translations.GetString ("Mode")));
	private ToolBarComboBox ModeComboBox {
		get {
			if (mode_combobox is null) {
				mode_combobox = ToolBarComboBox.New (100, 0, false, Translations.GetString ("Blur"), Translations.GetString ("Sharpen"));

				mode_combobox.ComboBox.OnChanged += (o, e) => {
					mode = (BlurSharpenMode) mode_combobox.ComboBox.Active;
				};

				mode_combobox.ComboBox.Active = Settings.GetSetting (SettingNames.BLUR_SHARPEN_MODE, 0);
			}

			return mode_combobox;
		}
	}

	private Label StrengthLabel => strength_label ??= Label.New (string.Format ("  {0}: ", Translations.GetString ("Strength")));
	private Scale StrengthSlider => strength_slider ??= GtkExtensions.CreateToolBarSlider (1, 100, 1, Settings.GetSetting (SettingNames.BLUR_SHARPEN_STRENGTH, 50));
	private Separator Separator => separator ??= GtkExtensions.CreateToolBarSeparator ();
}
