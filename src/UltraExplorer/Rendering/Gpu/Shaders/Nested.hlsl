// The nested canvas on the GPU.  Compiled at start-up by ShaderCache (vs_5_0 /
// ps_5_0, optimisation level 3) and kept in the state folder, so the build
// needs no Windows SDK and a second start compiles nothing.
//
// Everything is drawn as instanced quads with no vertex buffer of corners:
// the vertex shader makes a quad's four corners from SV_VertexID on a
// triangle strip, and the per-instance data says where the quad is and what
// to paint in it.  Coordinates are device pixels from the top-left of the
// frame; SV_Position in the pixel shader is the pixel's centre, so a quad
// edge on a whole pixel covers exactly the pixels the CPU raster fills.
//
// Three pipelines, drawn in the order the layers stack: rectangles (the
// scene's, then the label layer's pills and marks), icons, glyphs.  Every
// pixel shader returns premultiplied colour for the blend One /
// InverseSourceAlpha.

cbuffer Frame : register(b0)
{
    float2 ViewportPx;          // the part of the surface on screen, in pixels
    float2 InverseViewportPx;
    float Scale;                // the canvas's DPI scale
    float TextGamma;            // for the glyph pass: the gamma GammaRatios were made for
    float TextContrast;         // DirectWrite's grayscale enhanced contrast
    float TextSharpness;        // how many times steeper than one pixel a glyph's edge ramps
    float4 GammaRatios;         // DirectWrite's alpha correction for TextGamma
};

// ---- rectangles ---------------------------------------------------------------
//
// One pass draws the cells, the file tiles, the specks and washes, and the
// label layer's translucent rounded rectangles.  The shapes reproduce the
// raster's (Controls/NestedRaster.cs): every straight edge is a whole pixel,
// already snapped on the CPU exactly as the raster snaps it, and only the
// rounded corners are anti-aliased - here from the rounded box's signed
// distance, where the raster measures across each row.

static const uint KindPlain = 0u;
static const uint KindCell = 1u;
static const uint KindFile = 2u;
static const uint KindRounded = 3u;

static const uint HasBody = 1u << 8;
static const uint HasHeader = 1u << 9;
static const uint HasStripe = 1u << 10;

struct RectInstance
{
    float4 Outer : OUTER;               // left, top, right, bottom
    float4 Feature : FEATURE;           // the stripe
    float HeaderBottom : HEADERBOTTOM;
    float Radius : RADIUS;
    uint InnerRadii : INNERRADII;       // body | band << 16, half floats
    uint Kind : KIND;                   // kind | flags | body insets << 12
    uint4 Colours : COLOURS;            // body, rim, header, accent
};

struct RectVaryings
{
    float4 Position : SV_Position;
    nointerpolation float4 Outer : OUTER;
    nointerpolation float4 Feature : FEATURE;
    nointerpolation float HeaderBottom : HEADERBOTTOM;
    nointerpolation float Radius : RADIUS;
    nointerpolation uint InnerRadii : INNERRADII;
    nointerpolation uint Kind : KIND;
    nointerpolation uint4 Colours : COLOURS;
};

RectVaryings RectVS(RectInstance instance, uint vertex : SV_VertexID)
{
    // A shape with edges between pixels anti-aliases half a pixel outside
    // them too; its quad reaches a pixel further so those pixels are drawn.
    // Every other shape's edges are whole pixels, and its quad is its edges.
    float grow = (instance.Kind & 0xFFu) == KindRounded ? 1.0 : 0.0;
    float2 corner = float2(vertex & 1u, vertex >> 1);
    float2 p = lerp(instance.Outer.xy - grow, instance.Outer.zw + grow, corner);

    RectVaryings output;
    output.Position = float4(p * InverseViewportPx * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    output.Outer = instance.Outer;
    output.Feature = instance.Feature;
    output.HeaderBottom = instance.HeaderBottom;
    output.Radius = instance.Radius;
    output.InnerRadii = instance.InnerRadii;
    output.Kind = instance.Kind;
    output.Colours = instance.Colours;
    return output;
}

float4 Unpack(uint colour)
{
    return float4((colour >> 16) & 0xFFu, (colour >> 8) & 0xFFu, colour & 0xFFu, colour >> 24) / 255.0;
}

// Signed distance from p to a box with rounded corners: negative inside.
float RoundedBoxDistance(float2 p, float4 box, float radius)
{
    float2 centre = (box.xy + box.zw) * 0.5;
    float2 halfSize = (box.zw - box.xy) * 0.5;
    float2 q = abs(p - centre) - halfSize + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

// How much of the pixel centred on p the shape covers.  Exactly 1 or 0 for a
// pixel beside a straight edge on a whole pixel, as the raster's fills.
float Coverage(float2 p, float4 box, float radius)
{
    return saturate(0.5 - RoundedBoxDistance(p, box, radius));
}

// The title band: its top corners are the body's, its bottom edge square.
float TopRoundedCoverage(float2 p, float4 box, float radius)
{
    return Coverage(p, box, p.y < (box.y + box.w) * 0.5 ? radius : 0.0);
}

bool Inside(float2 p, float4 box)
{
    return p.x >= box.x && p.x < box.z && p.y >= box.y && p.y < box.w;
}

float4 RectPS(RectVaryings input) : SV_Target
{
    float2 p = input.Position.xy;
    uint kind = input.Kind & 0xFFu;
    float4 body = Unpack(input.Colours.x);

    if (kind == KindPlain)
    {
        return float4(body.rgb, 1.0);
    }

    if (kind == KindRounded)
    {
        float a = body.a * Coverage(p, input.Outer, input.Radius);
        return float4(body.rgb * a, a);
    }

    // The outline's own fill, premultiplied: the tile's body, or the cell's
    // rim.
    float outer = Coverage(p, input.Outer, input.Radius);
    float4 colour = float4((kind == KindCell ? Unpack(input.Colours.y).rgb : body.rgb) * outer, outer);
    if (kind == KindCell)
    {
        // FillFramed and the title band, in the raster's order and with its
        // arithmetic: the body one pixel in (as the raster finds that pixel:
        // 0, 1 or 2 per side) over the rim, then the band over the body,
        // each laid over what is under it by its own coverage - so a band
        // shorter than its corners are wide rounds tighter than the rim and
        // shows past it exactly where the raster's does.
        uint insets = input.Kind >> 12;
        float4 inner = input.Outer + float4(insets & 3u, (insets >> 2) & 3u, -float((insets >> 4) & 3u), -float((insets >> 6) & 3u));
        if ((input.Kind & HasBody) != 0u)
        {
            float cover = Coverage(p, inner, f16tof32(input.InnerRadii & 0xFFFFu));
            colour = float4(body.rgb * cover, cover) + colour * (1.0 - cover);
        }

        if ((input.Kind & HasHeader) != 0u)
        {
            float4 band = float4(inner.xyz, input.HeaderBottom);
            float cover = TopRoundedCoverage(p, band, f16tof32(input.InnerRadii >> 16));
            colour = float4(Unpack(input.Colours.z).rgb * cover, cover) + colour * (1.0 - cover);
        }
    }

    // The stripe is a plain fill over the rest, opaque whatever the corners.
    if ((input.Kind & HasStripe) != 0u && Inside(p, input.Feature))
    {
        colour = float4(Unpack(input.Colours.w).rgb, 1.0);
    }

    return colour;
}

// ---- icons ---------------------------------------------------------------------
//
// A file's icon: one slice of the icon atlas (IconAtlas), 64 pixels with its
// full mip chain, premultiplied.  Sampled trilinearly with a small negative
// bias so an icon drawn at 16 to 30 pixels leans on the sharper level - the
// hand-drawn 32 and 16 pixel frames where the Shell had them - rather than
// the blurrier one below.

Texture2DArray<float4> Icons : register(t0);
SamplerState IconSampler : register(s0);

struct IconInstance
{
    float4 Rect : RECT;                 // left, top, right, bottom in pixels
    uint Slot : SLOT;                   // the array slice
    uint Tint : TINT;                   // a premultiplied multiplier, 0xFFFFFFFF for none
};

struct IconVaryings
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
    nointerpolation float Slot : SLOT;
    nointerpolation float4 Tint : TINT;
};

IconVaryings IconVS(IconInstance instance, uint vertex : SV_VertexID)
{
    float2 corner = float2(vertex & 1u, vertex >> 1);
    float2 p = lerp(instance.Rect.xy, instance.Rect.zw, corner);

    IconVaryings output;
    output.Position = float4(p * InverseViewportPx * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    output.Uv = corner;
    output.Slot = instance.Slot;
    output.Tint = Unpack(instance.Tint);
    return output;
}

float4 IconPS(IconVaryings input) : SV_Target
{
    return Icons.SampleBias(IconSampler, float3(input.Uv, input.Slot), -0.4) * input.Tint;
}

// ---- glyphs --------------------------------------------------------------------
//
// Text: one quad per glyph over its signed distance field in the glyph atlas
// (GlyphAtlas) - one byte per texel, 0.5 on the outline, rising inside.  The
// field is stretched to whatever size the text is drawn at; PxRange says how
// many screen pixels its whole range spans at this size, so the distance in
// pixels is (v - 0.5) * PxRange, and coverage is that plus a half, clamped -
// exactly a pixel's worth of anti-aliasing across a straight edge, whatever
// the zoom.  Bias thickens small text a touch, where the field has no hinting
// to keep thin stems dark, and TextSharpness steepens every edge a little,
// which the text gate found brings the field's edges to the steepness of
// WPF's rasterised ones.
//
// What DirectWrite then does to grayscale text before it blends, the canvas
// does too (after lhecker/dwrite-hlsl, taken from Windows Terminal): an
// enhanced contrast that light text on a dark background is spared, and an
// alpha correction for the display gamma.  Without them light names on the
// dark cells read thinner than WPF's; the numbers (GpuTextTuning) are the
// ones the text gate found closest to WPF's own grayscale text.

Texture2DArray<float> Glyphs : register(t1);
SamplerState GlyphSampler : register(s1);

static const float GlyphPageSize = 2048.0;

struct GlyphInstance
{
    float4 Rect : RECT;                 // left, top, right, bottom in pixels
    uint Uv0 : UVMIN;                   // top-left texel, u | v << 16
    uint Uv1 : UVMAX;                   // bottom-right texel, exclusive
    uint Colour : COLOUR;               // straight 0xAARRGGBB
    uint PageTier : PAGETIER;           // page | tier << 8
    float PxRange : PXRANGE;
    float Bias : BIAS;
};

struct GlyphVaryings
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
    nointerpolation float Page : PAGE;
    nointerpolation float4 Colour : COLOUR;
    nointerpolation float PxRange : PXRANGE;
    nointerpolation float Bias : BIAS;
};

GlyphVaryings GlyphVS(GlyphInstance instance, uint vertex : SV_VertexID)
{
    float2 corner = float2(vertex & 1u, vertex >> 1);
    float2 p = lerp(instance.Rect.xy, instance.Rect.zw, corner);
    float2 uv0 = float2(instance.Uv0 & 0xFFFFu, instance.Uv0 >> 16);
    float2 uv1 = float2(instance.Uv1 & 0xFFFFu, instance.Uv1 >> 16);

    GlyphVaryings output;
    output.Position = float4(p * InverseViewportPx * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    output.Uv = lerp(uv0, uv1, corner) / GlyphPageSize;
    output.Page = instance.PageTier & 0xFFu;
    output.Colour = Unpack(instance.Colour);
    output.PxRange = instance.PxRange;
    output.Bias = instance.Bias;
    return output;
}

float TextIntensity(float3 colour)
{
    return dot(colour, float3(0.25, 0.5, 0.25));
}

// DirectWrite's grayscale blend: contrast enhanced (less the lighter the
// text), then the alpha correction for the gamma the ratios were made for.
float TextAlpha(float alpha, float3 colour)
{
    float intensity = TextIntensity(colour);
    float contrast = TextContrast * saturate(4.0 * (0.75 - intensity));
    float enhanced = alpha * (contrast + 1.0) / (alpha * contrast + 1.0);
    float4 g = GammaRatios;
    return saturate(enhanced + enhanced * (1.0 - enhanced) * ((g.x * intensity + g.y) * enhanced + (g.z * intensity + g.w)));
}

float4 GlyphPS(GlyphVaryings input) : SV_Target
{
    float field = Glyphs.Sample(GlyphSampler, float3(input.Uv, input.Page));
    float coverage = saturate((field - 0.5) * input.PxRange * TextSharpness + 0.5 + input.Bias);
    float alpha = TextAlpha(coverage, input.Colour.rgb) * input.Colour.a;
    return float4(input.Colour.rgb * alpha, alpha);
}
