using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// Shader property introspection as the original's player gives it (UnityProbe's apigaps
    /// "shader", U59's GLCore player at the main menu; 77 shaders loaded in both players matched).
    /// Each property: index, name, type, flags, description, attributes, then what
    /// GetPropertyDefaultFloatValue, GetPropertyDefaultVectorValue, GetPropertyRangeLimits,
    /// GetPropertyTextureDimension and GetPropertyTextureDefaultName give ("!" and the
    /// ArgumentException's message when they throw).
    public class ShaderPropertyTests : EngineTest
    {
        static readonly string[] Measured =
        [
            "S|Klei/Liquid|56",
            "P|0|_LiquidSelectStart|Range|0|liquid start||0.438|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|1|_LiquidSelectRangeNear|Range|0|liquid range near||0.438|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|2|_LiquidSelectRangeFar|Range|0|liquid range far||0.438|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|3|_LiquidSelectRangeNearOrthoSize|Range|0|liquid range near ortho size||0.438|!Property type is not Color or Vector.|(0,200)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|4|_LiquidSelectRangeFarOrthoSize|Range|0|liquid range far ortho size||0.438|!Property type is not Color or Vector.|(0,200)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|5|_DebugInspect|Float|0|Debug Inspect|Header(Debug)|Toggle(DEBUG_INSPECT)|1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|6|_TextureColumnSliceCount|Float|0|Textures Column Slice Count|Header(Textures Settings)|8|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|7|_Opacity|Range|0|Opacity|Header(Visibility Settings)|0.77|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|8|_OpaqueLiquidsOpacity|Range|0|Opaque Liquids Opacity||0.95|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|9|_ZoomedOutOpacity|Range|0|Zoomed Out Inspection Opacity|Header(Inspection Settings)|0.6|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|10|_ZoomedInOpacity|Range|0|Zoomed In Inspection Opacity||0.3|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|11|_ZoomedOutOpacityInfluenceRadius|Float|0|Zoomed Out Inspection Radius||20|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|12|_ZoomedInOpacityInfluenceRadius|Float|0|Zoomed In Inspection Radius||10|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|13|_SampleFiltering|Float|0|SampleFiltering|Header(Sample Filtering)|KeywordEnum(Bicubic Hermite, Bilinear, None)|0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|14|_EnableCaustics|Float|0|Enable Caustics|Header(Caustics)|Toggle(ENABLE_CAUSTIC)|1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|15|_CausticSpeed|Range|0|caustic speed||1.21|!Property type is not Color or Vector.|(0,10)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|16|_CausticFrequency|Range|0|caustic frequency||0.5|!Property type is not Color or Vector.|(0,2)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|17|_CausticCount|Range|0|caustic count||5|!Property type is not Color or Vector.|(2,10)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|18|_CausticScale|Range|0|caustic scale||1.4|!Property type is not Color or Vector.|(0,10)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|19|_EnableWaves|Float|0|Enable Waves|Header(Surface Waves)|Toggle(ENABLE_WAVE)|1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|20|_WaveSpeed|Range|0|wave speed||1|!Property type is not Color or Vector.|(0,10)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|21|_WaveFrequency|Range|0|wave frequency||1|!Property type is not Color or Vector.|(0,10)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|22|_WaveCount|Range|0|wave count||5|!Property type is not Color or Vector.|(2,10)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|23|_WaveHeight|Range|0|wave height||5|!Property type is not Color or Vector.|(0,20)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|24|_DistortionStrength|Range|0|strength|Header(Distortion)|0.018|!Property type is not Color or Vector.|(0,0.8)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|25|_DistortionFrequency|Range|0|frequency||1|!Property type is not Color or Vector.|(0,5)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|26|_DistortionSpeed|Range|0|speed||1|!Property type is not Color or Vector.|(0,5)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|27|_BoilingTreshold|Range|0|boiling treshold|Header(Temperature Effects)|0.94|!Property type is not Color or Vector.|(0.6,0.998)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|28|_FreezingTreshold|Range|0|freezing treshold||0.1|!Property type is not Color or Vector.|(0.001,0.4)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|29|_BoilOverride_DistortionStrength|Range|0|boiling distortion strength override|Space|0.048|!Property type is not Color or Vector.|(0,0.1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|30|_BoilOverride_DistortionFrequency|Range|0|boiling distortion frequency override||0.84|!Property type is not Color or Vector.|(0,5)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|31|_BoilOverride_DistortionSpeed|Range|0|boiling distortion speed override||1.002|!Property type is not Color or Vector.|(0.8,1.1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|32|_EnableFreezingSqr|Float|0|Non Linear Freeze|Space|Toggle(FREEZING_NON_LINEAR)|1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|33|_FreezeOverride_SolidColorStrength|Range|0|freezing color strength||0.4|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|34|_FreezeOverride_OpacityStrength|Range|0|freezing opacity strength||0.6|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|35|_FreezeOverride_DistortionStrength|Range|0|freezing distortion strength override||0|!Property type is not Color or Vector.|(0,0.1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|36|_FreezeOverride_DistortionFrequency|Range|0|freezing distortion frequency override||0|!Property type is not Color or Vector.|(0,5)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|37|_FreezeOverride_DistortionSpeed|Range|0|freezing distortion speed override||0|!Property type is not Color or Vector.|(0,5)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|38|_MoltenMetalColor|Color|0|Molten Metal Color|Header(Molten Metals)|!Property type is not Float or Range.|(1,1,0.3,1)|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|39|_AirBubbleMinBoilingPointRequired|Range|0|air bubble sample 1 visibility treshold|Header(Boiling Bubbles)|Space|0.7|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|40|_AirBubbleLargeMinBoilingPointRequired|Range|0|air bubble sample 2 visibility treshold||0.85|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|41|_BubbleDistortionStrengthModifier|Range|0|bubble distortion strength modifier|Space|7|!Property type is not Color or Vector.|(0,10)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|42|_DistortionBubbles_Sample1_SizeModifier|Range|0|distortion bubble sample 1 size|Space|1|!Property type is not Color or Vector.|(0,10)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|43|_DistortionBubbles_Sample1_SpeedModifier|Range|0|distortion bubble sample 1 speed||0.9|!Property type is not Color or Vector.|(0,4)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|44|_DistortionBubbles_Sample2_SizeModifier|Range|0|distortion bubble sample 2 size|Space|5|!Property type is not Color or Vector.|(0,10)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|45|_DistortionBubbles_Sample2_SpeedModifier|Range|0|distortion bubble sample 2 speed||0.3|!Property type is not Color or Vector.|(0,4)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|46|_AirBubbles_Sample1_MaxOpacity|Range|0|air bubble sample 1 max opacity|Space|1|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|47|_AirBubbles_Sample2_MaxOpacity|Range|0|air bubble sample 2 max opacity||1|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|48|_AirBubbles_Sample1_SizeModifier|Range|0|air bubble sample 1 size|Space|0.83333|!Property type is not Color or Vector.|(0,10)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|49|_AirBubbles_Sample1_SpeedModifier|Range|0|air bubble sample 1 speed||1|!Property type is not Color or Vector.|(0,4)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|50|_AirBubbles_Sample2_SizeModifier|Range|0|air bubble sample 2 size|Space|1.25|!Property type is not Color or Vector.|(0,10)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|51|_AirBubbles_Sample2_SpeedModifier|Range|0|air bubble sample 2 speed||1.1|!Property type is not Color or Vector.|(0,4)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|52|_BoilingBubblesTex|Texture|0|boiling Bubbles Texture|Space|!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|53|_Textures|Texture|0|Textures||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2DArray|",
            "P|54|_SpecularMapTextures|Texture|0|SpecularMap Textures||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2DArray|",
            "P|55|_NormalNoise|Texture|0|Normal Noise|Header(Shine)|!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "S|Klei/Stars |17",
            "P|0|_EnableStarTravelling|Float|0|Enable Star Travel|Toggle(ENABLE_STAR_TRAVELLING)|1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|1|_TravelStars|Texture|0|Travel Stars||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|2|_Tex0|Texture|0|Tex0 (RGB)||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|3|_Tex1|Texture|0|Tex1 (RGB)||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|4|_Tex2|Texture|0|Tex2 (RGB)||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|5|_TexDay0|Texture|0|TexDay0 (RGB)||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|6|_TexDay1|Texture|0|TeDayx1 (RGB)||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|7|_TexDay2|Texture|0|TexDay2 (RGB)||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|8|_Parallax0|Float|0|Parallax 0||1.025|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|9|_Parallax1|Float|0|Parallax 1||1.1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|10|_StarTravelAcceleration|Float|0|Star Travel acceleration||1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|11|_StarTravelMaxSpeed|Float|0|Star travel Max Speed||1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|12|_Tint0|Color|0|Tint0||!Property type is not Float or Range.|(1,1,1,1)|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|13|_Tint1|Color|0|Tint1||!Property type is not Float or Range.|(1,1,1,1)|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|14|_Tint2|Color|0|Tint1||!Property type is not Float or Range.|(1,1,1,1)|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|15|_TimeOfDayOverride|Range|0|Time Of Day Override||-1|!Property type is not Color or Vector.|(-1,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|16|_TimeOfDayMultiplier|Float|0|Time Of Day Multiplier||1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "S|Sprites/Default|7",
            "P|0|_MainTex|Texture|2|Sprite Texture||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|1|_Color|Color|0|Tint||!Property type is not Float or Range.|(1,1,1,1)|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|2|PixelSnap|Float|0|Pixel snap|MaterialToggle|0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|3|_RendererColor|Color|1|RendererColor||!Property type is not Float or Range.|(1,1,1,1)|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|4|_Flip|Vector|1|Flip||!Property type is not Float or Range.|(1,1,1,1)|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|5|_AlphaTex|Texture|2|External Alpha||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|6|_EnableExternalAlpha|Float|2|Enable External Alpha||0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "S|Standard|27",
            "P|0|_Color|Color|0|Color||!Property type is not Float or Range.|(1,1,1,1)|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|1|_MainTex|Texture|0|Albedo||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|2|_Cutoff|Range|0|Alpha Cutoff||0.5|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|3|_Glossiness|Range|0|Smoothness||0.5|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|4|_GlossMapScale|Range|0|Smoothness Scale||1|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|5|_SmoothnessTextureChannel|Float|0|Smoothness texture channel|Enum(Metallic Alpha,0,Albedo Alpha,1)|0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|6|_Metallic|Range|32|Metallic||0|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|7|_MetallicGlossMap|Texture|0|Metallic||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|8|_SpecularHighlights|Float|0|Specular Highlights|ToggleOff|1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|9|_GlossyReflections|Float|0|Glossy Reflections|ToggleOff|1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|10|_BumpScale|Float|0|Scale||1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|11|_BumpMap|Texture|8|Normal Map||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|bump",
            "P|12|_Parallax|Range|0|Height Scale||0.02|!Property type is not Color or Vector.|(0.005,0.08)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|13|_ParallaxMap|Texture|0|Height Map||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|black",
            "P|14|_OcclusionStrength|Range|0|Strength||1|!Property type is not Color or Vector.|(0,1)|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|15|_OcclusionMap|Texture|0|Occlusion||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|16|_EmissionColor|Color|0|Color||!Property type is not Float or Range.|(0,0,0,1)|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|17|_EmissionMap|Texture|0|Emission||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|18|_DetailMask|Texture|0|Detail Mask||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|19|_DetailAlbedoMap|Texture|0|Detail Albedo x2||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|grey",
            "P|20|_DetailNormalMapScale|Float|0|Scale||1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|21|_DetailNormalMap|Texture|8|Normal Map||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|bump",
            "P|22|_UVSec|Float|0|UV Set for secondary textures|Enum(UV0,0,UV1,1)|0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|23|_Mode|Float|1|__mode||0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|24|_SrcBlend|Float|1|__src||1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|25|_DstBlend|Float|1|__dst||0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|26|_ZWrite|Float|1|__zw||1|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "S|TextMeshPro/Sprite|11",
            "P|0|_MainTex|Texture|0|Sprite Texture||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|1|_Color|Color|0|Tint||!Property type is not Float or Range.|(1,1,1,1)|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|2|_StencilComp|Float|0|Stencil Comparison||8|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|3|_Stencil|Float|0|Stencil ID||0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|4|_StencilOp|Float|0|Stencil Operation||0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|5|_StencilWriteMask|Float|0|Stencil Write Mask||255|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|6|_StencilReadMask|Float|0|Stencil Read Mask||255|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|7|_CullMode|Float|0|Cull Mode||0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|8|_ColorMask|Float|0|Color Mask||15|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|9|_ClipRect|Vector|0|Clip Rect||!Property type is not Float or Range.|(-32767,-32767,32767,32767)|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|10|_UseUIAlphaClip|Float|0|Use Alpha Clip|Toggle(UNITY_UI_ALPHACLIP)|0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "S|UI/Default|9",
            "P|0|_MainTex|Texture|2|Sprite Texture||!Property type is not Float or Range.|!Property type is not Color or Vector.|!Property type is not Range.|Tex2D|white",
            "P|1|_Color|Color|0|Tint||!Property type is not Float or Range.|(1,1,1,1)|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|2|_StencilComp|Float|0|Stencil Comparison||8|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|3|_Stencil|Float|0|Stencil ID||0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|4|_StencilOp|Float|0|Stencil Operation||0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|5|_StencilWriteMask|Float|0|Stencil Write Mask||255|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|6|_StencilReadMask|Float|0|Stencil Read Mask||255|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|7|_ColorMask|Float|0|Color Mask||15|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
            "P|8|_UseUIAlphaClip|Float|0|Use Alpha Clip|Toggle(UNITY_UI_ALPHACLIP)|0|!Property type is not Color or Vector.|!Property type is not Range.|!Property type is not TexEnv.|!Property type is not Texture.",
        ];

        static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);

        static string Try(Func<string> f)
        {
            try { return f(); }
            catch (ArgumentException e) when (e is not ArgumentOutOfRangeException) { return "!" + e.Message; }
        }

        static string Describe(Shader s, int i) => string.Join("|",
            "P", i.ToString(CultureInfo.InvariantCulture), s.GetPropertyName(i), s.GetPropertyType(i).ToString(),
            ((int)s.GetPropertyFlags(i)).ToString(CultureInfo.InvariantCulture), s.GetPropertyDescription(i), string.Join("|", s.GetPropertyAttributes(i)),
            Try(() => F(s.GetPropertyDefaultFloatValue(i))),
            Try(() => { var v = s.GetPropertyDefaultVectorValue(i); return $"({F(v.x)},{F(v.y)},{F(v.z)},{F(v.w)})"; }),
            Try(() => { var r = s.GetPropertyRangeLimits(i); return $"({F(r.x)},{F(r.y)})"; }),
            Try(() => s.GetPropertyTextureDimension(i).ToString()),
            Try(() => s.GetPropertyTextureDefaultName(i)));

        [Fact]
        public void PropertiesReadAsTheOriginalGivesThem()
        {
            AssetSystem.Initialize(GameInstall.DataDir);
            ProjectSettings.Load();
            var wrong = new List<string>();
            Shader shader = null;
            foreach (var line in Measured)
            {
                var f = line.Split('|');
                if (f[0] == "S")
                {
                    shader = Shader.Find(f[1]);
                    if (shader == null) { wrong.Add("no shader " + f[1]); continue; }
                    if (shader.GetPropertyCount().ToString(CultureInfo.InvariantCulture) != f[2]) wrong.Add($"{f[1]}: {shader.GetPropertyCount()} properties, the original {f[2]}");
                    continue;
                }
                if (shader == null) continue;
                int i = int.Parse(f[1], CultureInfo.InvariantCulture);
                string ours = Describe(shader, i);
                if (ours != line) wrong.Add(shader.name + "\n  original " + line + "\n  ours     " + ours);
                if (shader.FindPropertyIndex(f[2]) != i) wrong.Add($"{shader.name}: FindPropertyIndex({f[2]}) = {shader.FindPropertyIndex(f[2])}");
                if (shader.GetPropertyNameId(i) != Shader.PropertyToID(f[2])) wrong.Add($"{shader.name}: GetPropertyNameId({i})");
            }
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        [Fact]
        public void FindPropertyIndexIsCaseSensitiveAndMinusOneForNone()
        {
            AssetSystem.Initialize(GameInstall.DataDir);
            ProjectSettings.Load();
            var s = Shader.Find("UI/Default");
            Assert.Equal(0, s.FindPropertyIndex("_MainTex"));
            Assert.Equal(-1, s.FindPropertyIndex("_MAINTEX"));
            Assert.Equal(-1, s.FindPropertyIndex("nope"));
            Assert.Equal(-1, s.FindPropertyIndex(""));
            Assert.Equal(-1, s.FindPropertyIndex(null));
            int n = s.GetPropertyCount();
            foreach (int bad in new[] { -1, n })
            {
                Assert.Equal("propertyIndex", Assert.Throws<ArgumentOutOfRangeException>(() => s.GetPropertyNameId(bad)).ParamName);
                Assert.Throws<ArgumentOutOfRangeException>(() => s.GetPropertyFlags(bad));
                Assert.Throws<ArgumentOutOfRangeException>(() => s.GetPropertyDefaultFloatValue(bad));
                Assert.Throws<ArgumentOutOfRangeException>(() => s.GetPropertyTextureDefaultName(bad));
            }
            Assert.Equal("Property type is not Int.", Assert.Throws<ArgumentException>(() => s.GetPropertyDefaultIntValue(0)).Message);
        }
    }
}
