using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Silk.NET.OpenGL;
using System;
using System.Diagnostics;

namespace ASE
{
    public class GLControl : OpenGlControlBase
    {
        // Shaders — Desktop OpenGL Core Profile. The "#version" line is prepended at init
        // (see DesktopGlslVersion): the body only needs GLSL 1.40 features, and drivers that
        // stop at GL 3.1 — the Raspberry Pi's V3D, for one — reject "#version 150" outright.
        private const string VertexShaderDesktop = @"
            in vec2 aPos;
            in vec2 aTexCoord;
            out vec2 v_texCoord;

            void main() {
                gl_Position = vec4(aPos, 0.0, 1.0);
                v_texCoord  = aTexCoord;
            }";

        private const string FragmentShaderDesktop = @"
            in vec2 v_texCoord;
            out vec4 fragColor;
            uniform sampler2D uTexture;

            uniform vec2 uSourceSize;
            uniform vec2 uOutputSize;
            uniform float uTime;
            uniform float uCurvature;
            uniform float uVignette;
            uniform float uScanline;
            uniform float uChromAb;
            uniform float uBloom;
            uniform float uMask;
            uniform float uNoise;
            uniform vec2 uTexMin;
            uniform vec2 uTexMax;

            float hash12(vec2 p) {
                vec3 p3  = fract(vec3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return fract((p3.x + p3.y) * p3.z);
            }

            vec3 texRGB(vec2 uv) {
                return texture(uTexture, uv).rgb;
            }

            void main() {
                vec2 uv = v_texCoord;
                float aspect = uOutputSize.x / uOutputSize.y;

                vec2 p = uv * 2.0 - 1.0;
                p.x *= aspect;
                float r2 = dot(p, p);
                p *= (1.0 + uCurvature * r2);
                p.x /= aspect;
                uv = p * 0.5 + 0.5;

                float inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
                float feather = 0.010;
                float edge =
                    smoothstep(0.0, feather, uv.x) *
                    smoothstep(0.0, feather, uv.y) *
                    smoothstep(0.0, feather, 1.0 - uv.x) *
                    smoothstep(0.0, feather, 1.0 - uv.y);
                float screenMask = inside * edge;

                // Optional border crop: when borders are hidden, remap the screen UVs to the
                // 320x200 display sub-rectangle of the texture (identity when borders are shown).
                uv = mix(uTexMin, uTexMax, uv);

                float dist = sqrt(r2);
                vec2 dir = normalize(p + vec2(1e-4));
                float ca = uChromAb * (0.0006 + 0.0018 * dist * dist);
                vec2 caOff = dir * ca;

                vec3 col;
                col.r = texture(uTexture, uv + caOff).r;
                col.g = texture(uTexture, uv).g;
                col.b = texture(uTexture, uv - caOff).b;

                vec3 lin = pow(col, vec3(2.2));
                vec2 t = 1.0 / max(uSourceSize, vec2(1.0));
                vec3 b =
                    texRGB(uv) * 0.40 +
                    texRGB(uv + vec2( t.x, 0.0)) * 0.15 +
                    texRGB(uv + vec2(-t.x, 0.0)) * 0.15 +
                    texRGB(uv + vec2(0.0,  t.y)) * 0.15 +
                    texRGB(uv + vec2(0.0, -t.y)) * 0.15;
                b = pow(b, vec3(2.2));
                vec3 bright = max(b - vec3(0.60), vec3(0.0));
                lin += bright * (1.0 * uBloom);

                float y = uv.y * uSourceSize.y;
                float scan = 0.92 + 0.08 * sin(6.2831853 * (y + 0.15));
                lin *= mix(1.0, scan, uScanline);

                float px = floor(gl_FragCoord.x);
                float py = floor(gl_FragCoord.y);
                float row = mod(py, 2.0);
                float shift = (row > 0.5) ? 1.5 : 0.0;
                float parts = mod(px + shift, 3.0);
                vec3 dots = vec3(0.25);
                if (parts < 1.0) dots.r = 1.0;
                else if (parts < 2.0) dots.g = 1.0;
                else dots.b = 1.0;
                lin *= mix(vec3(1.0), dots, uMask);

                float vig = 1.0 - uVignette * smoothstep(0.55, 1.25, dist);
                lin *= vig;

                float n = hash12(gl_FragCoord.xy + uTime * 60.0);
                lin += (n - 0.5) * 0.02 * uNoise;

                vec3 outCol = pow(max(lin, 0.0), vec3(1.0 / 2.2));
                outCol = mix(vec3(0.0), outCol, screenMask);

                fragColor = vec4(outCol, 1.0);
            }";

        // Shaders — OpenGL ES 2.0 / 3.0  (ANGLE / Metal). GLSL ES 1.00 is accepted by every
        // ES context, including ES 3.x, so the "#version 100" line is fixed.
        private const string VertexShaderES = @"#version 100
            precision highp float;

            attribute vec2 aPos;
            attribute vec2 aTexCoord;
            varying vec2 v_texCoord;

            void main() {
                gl_Position = vec4(aPos, 0.0, 1.0);
                v_texCoord  = aTexCoord;
            }";

        private const string FragmentShaderES = @"#version 100
            precision highp float;

            varying vec2 v_texCoord;
            uniform sampler2D uTexture;

            uniform vec2 uSourceSize;
            uniform vec2 uOutputSize;
            uniform float uTime;
            uniform float uCurvature;
            uniform float uVignette;
            uniform float uScanline;
            uniform float uChromAb;
            uniform float uBloom;
            uniform float uMask;
            uniform float uNoise;
            uniform vec2 uTexMin;
            uniform vec2 uTexMax;

            float hash12(vec2 p) {
                vec3 p3  = fract(vec3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return fract((p3.x + p3.y) * p3.z);
            }

            vec3 texRGB(vec2 uv) {
                return texture2D(uTexture, uv).rgb;
            }

            void main() {
                vec2 uv = v_texCoord;
                float aspect = uOutputSize.x / uOutputSize.y;

                vec2 p = uv * 2.0 - 1.0;
                p.x *= aspect;
                float r2 = dot(p, p);
                p *= (1.0 + uCurvature * r2);
                p.x /= aspect;
                uv = p * 0.5 + 0.5;

                float inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
                float feather = 0.010;
                float edge =
                    smoothstep(0.0, feather, uv.x) *
                    smoothstep(0.0, feather, uv.y) *
                    smoothstep(0.0, feather, 1.0 - uv.x) *
                    smoothstep(0.0, feather, 1.0 - uv.y);
                float screenMask = inside * edge;

                // Optional border crop: when borders are hidden, remap the screen UVs to the
                // 320x200 display sub-rectangle of the texture (identity when borders are shown).
                uv = mix(uTexMin, uTexMax, uv);

                float dist = sqrt(r2);
                vec2 dir = normalize(p + vec2(1e-4));
                float ca = uChromAb * (0.0006 + 0.0018 * dist * dist);
                vec2 caOff = dir * ca;

                vec3 col;
                col.r = texture2D(uTexture, uv + caOff).r;
                col.g = texture2D(uTexture, uv).g;
                col.b = texture2D(uTexture, uv - caOff).b;

                vec3 lin = pow(col, vec3(2.2));
                vec2 t = 1.0 / max(uSourceSize, vec2(1.0));
                vec3 b =
                    texRGB(uv) * 0.40 +
                    texRGB(uv + vec2( t.x, 0.0)) * 0.15 +
                    texRGB(uv + vec2(-t.x, 0.0)) * 0.15 +
                    texRGB(uv + vec2(0.0,  t.y)) * 0.15 +
                    texRGB(uv + vec2(0.0, -t.y)) * 0.15;
                b = pow(b, vec3(2.2));
                vec3 bright = max(b - vec3(0.60), vec3(0.0));
                lin += bright * (1.0 * uBloom);

                float y = uv.y * uSourceSize.y;
                float scan = 0.92 + 0.08 * sin(6.2831853 * (y + 0.15));
                lin *= mix(1.0, scan, uScanline);

                float px = floor(gl_FragCoord.x);
                float py = floor(gl_FragCoord.y);
                float row = mod(py, 2.0);
                float shift = (row > 0.5) ? 1.5 : 0.0;
                float parts = mod(px + shift, 3.0);
                vec3 dots = vec3(0.25);
                if (parts < 1.0) dots.r = 1.0;
                else if (parts < 2.0) dots.g = 1.0;
                else dots.b = 1.0;
                lin *= mix(vec3(1.0), dots, uMask);

                float vig = 1.0 - uVignette * smoothstep(0.55, 1.25, dist);
                lin *= vig;

                float n = hash12(gl_FragCoord.xy + uTime * 60.0);
                lin += (n - 0.5) * 0.02 * uNoise;

                vec3 outCol = pow(max(lin, 0.0), vec3(1.0 / 2.2));
                outCol = mix(vec3(0.0), outCol, screenMask);

                gl_FragColor = vec4(outCol, 1.0);
            }";

        // Shaders — "plain" path: the same blit with no post-processing at all, used when
        // Config.DisableCrtEffects is on. It is a separate program rather than the CRT one with
        // every uniform at 0 because a weight of 0 costs the GPU exactly the same as any other
        // value: the fragment program still runs the five bloom taps, the noise hash and the two
        // gamma conversions for every pixel on screen. On a Raspberry Pi's V3D that is the
        // difference worth having. The border crop (uTexMin/uTexMax) is kept — it is a UV remap,
        // not an effect. Vertex shaders are shared with the CRT path.
        private const string FragmentShaderPlainDesktop = @"
            in vec2 v_texCoord;
            out vec4 fragColor;
            uniform sampler2D uTexture;
            uniform vec2 uTexMin;
            uniform vec2 uTexMax;

            void main() {
                fragColor = vec4(texture(uTexture, mix(uTexMin, uTexMax, v_texCoord)).rgb, 1.0);
            }";

        private const string FragmentShaderPlainES = @"#version 100
            precision highp float;

            varying vec2 v_texCoord;
            uniform sampler2D uTexture;
            uniform vec2 uTexMin;
            uniform vec2 uTexMax;

            void main() {
                gl_FragColor = vec4(texture2D(uTexture, mix(uTexMin, uTexMax, v_texCoord)).rgb, 1.0);
            }";

        // =====================================================================

        /// <summary>
        /// A linked GL program together with its uniform locations. A location of -1 means the
        /// uniform is not present in that program (the effect uniforms in the plain shader, or
        /// anything the driver optimised away), which the render path already treats as "skip".
        /// </summary>
        private sealed class ShaderProgram
        {
            public uint Id;
            public bool Valid;

            public int Texture = -1, SourceSize = -1, OutputSize = -1, Time = -1;
            public int Curvature = -1, Vignette = -1, Scanline = -1, ChromAb = -1;
            public int Bloom = -1, Mask = -1, Noise = -1;
            public int TexMin = -1, TexMax = -1;
        }

        private GL _gl;
        private uint _textureId;
        private ShaderProgram _crtProgram;
        private ShaderProgram _plainProgram;
        private uint _vao;
        private uint _vbo;
        private bool _hasVao;
        private bool _firstRender = true;
        private double _lastLoggedScaling = -1;

        // The TopLevel this control hangs from, cached on attach: it is the only object that
        // reports the DPI scale. Do *not* go through VisualRoot — see ScreenScaling below.
        private Avalonia.Controls.TopLevel _topLevel;

        private readonly Stopwatch _timer = new Stopwatch();

        // Atari screen size (the active video geometry). Runtime, not compile-time: a colour
        // monitor is 832x288 and a monochrome one 640x400, and the user can switch between them
        // (through a reset). The texture is resized to match whenever VideoTiming's geometry
        // generation changes; see EnsureTextureGeometry.
        private int _srcW = VideoTiming.BUFFER_WIDTH;
        private int _srcH = VideoTiming.BUFFER_HEIGHT;
        private int _geomGen = -1;   // -1 forces the first render to size the texture

        // Pixel aspect ratio of the ST on an original 4:3 PAL monitor: the tube shows
        // ~52 µs of active line over ~288 visible lines, so the 416 low-res pixels (8 MHz)
        // that fit in 52 µs span 288 * 4/3 = 384 line-height units -> each pixel is
        // 384/416 = 12/13 as wide as it is tall (slightly narrower than square).
        public const double PIXEL_ASPECT = 12.0 / 13.0;

        /// <summary>Aspect ratio of the picture this control shows. On a colour monitor it is the
        /// full framebuffer (display + borders) or the 640x400 crop when borders are hidden,
        /// corrected by the pixel aspect of the original 4:3 monitor. On a monochrome monitor it
        /// is the native 640x400 with square pixels.
        /// <para>It belongs here rather than to the window because it is what the letterbox in
        /// <see cref="OnOpenGlRender"/> fits the picture to; the window's own resize logic
        /// (MainWindow.EnforceAspectRatio) reads the same value so both agree.</para></summary>
        public static double DisplayAspectRatio =>
            VideoTiming.Mono
                ? (double)VideoTiming.BUFFER_WIDTH / VideoTiming.BUFFER_HEIGHT
                : PIXEL_ASPECT * (Config.ConfigOptions.RunninConfig.ShowBorders
                    ? (double)VideoTiming.BUFFER_WIDTH / (VideoTiming.BUFFER_HEIGHT * 2)
                    : (double)VideoTiming.DISPLAY_TEX_WIDTH / (VideoTiming.DISPLAY_TEX_HEIGHT * 2));

        // Sequence number of the frame currently in the texture. The GL thread renders free-running
        // and normally beats the 50 Hz emulation, so this is what stops it from re-uploading the
        // same ~940 KB several times per emulated frame.
        private long _frameSeq;

        private bool CheckShader(uint shader, string name)
        {
            _gl.GetShader(shader, GLEnum.CompileStatus, out int status);
            if (status == 0)
            {
                string log = _gl.GetShaderInfoLog(shader);
                Console.WriteLine($"[GLControl] ERROR compilando {name}: {log}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// DPI scale of the screen the control is on (1.25 at Windows' 125% setting, 1.0 at 100%).
        /// <para>
        /// It must be read from the <see cref="Avalonia.Controls.TopLevel"/>, never from
        /// <c>VisualRoot</c>: in Avalonia 12 a control's visual root is a
        /// <c>Avalonia.Controls.TopLevelHost</c>, which does **not** derive from <c>TopLevel</c>,
        /// so the old <c>VisualRoot as TopLevel</c> cast silently returned null and the scale fell
        /// back to 1.0. Avalonia still sized the framebuffer as <c>Bounds * RenderScaling</c>, so at
        /// 125% the viewport covered only 1/1.25 = 80% of it and the picture stopped short of the
        /// window edges (bottom-left corner, the rest left at the clear colour).
        /// </para>
        /// </summary>
        private double ScreenScaling =>
            (_topLevel ??= Avalonia.Controls.TopLevel.GetTopLevel(this))?.RenderScaling ?? 1.0;

        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            _topLevel = null;
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>
        /// GLSL version to ask the desktop shaders for, derived from what the driver reports in
        /// <c>GL_SHADING_LANGUAGE_VERSION</c> ("1.40", "3.30 NVIDIA…", "4.60 …").
        /// <para>
        /// Only two answers matter: <b>150</b> (GL 3.2+) and <b>140</b> (GL 3.1). The shader body
        /// uses nothing newer than 1.40, but the choice cannot be fixed either way — the Raspberry
        /// Pi's V3D driver caps at GL 3.1/GLSL 1.40 and rejects <c>#version 150</c>, while macOS
        /// core profiles reject anything *below* 150.
        /// </para>
        /// </summary>
        private int DesktopGlslVersion(string glslVersionString)
        {
            // Leading "<major>.<minor>" of the version string, ignoring the vendor suffix.
            string head = (glslVersionString ?? "").TrimStart().Split(' ')[0];
            string[] parts = head.Split('.');

            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out int major) &&
                int.TryParse(parts[1].Length >= 2 ? parts[1].Substring(0, 2) : parts[1] + "0", out int minor))
            {
                return major * 100 + minor >= 150 ? 150 : 140;
            }

            // Unparseable string: fall back to the context Avalonia created (GL 3.2 -> GLSL 1.50).
            var v = this.GlVersion;
            return (v.Major > 3 || (v.Major == 3 && v.Minor >= 2)) ? 150 : 140;
        }

        private void CacheUniformLocations(ShaderProgram p)
        {
            p.Texture    = _gl.GetUniformLocation(p.Id, "uTexture");
            p.SourceSize = _gl.GetUniformLocation(p.Id, "uSourceSize");
            p.OutputSize = _gl.GetUniformLocation(p.Id, "uOutputSize");
            p.Time       = _gl.GetUniformLocation(p.Id, "uTime");
            p.Curvature  = _gl.GetUniformLocation(p.Id, "uCurvature");
            p.Vignette   = _gl.GetUniformLocation(p.Id, "uVignette");
            p.Scanline   = _gl.GetUniformLocation(p.Id, "uScanline");
            p.ChromAb    = _gl.GetUniformLocation(p.Id, "uChromAb");
            p.Bloom      = _gl.GetUniformLocation(p.Id, "uBloom");
            p.Mask       = _gl.GetUniformLocation(p.Id, "uMask");
            p.Noise      = _gl.GetUniformLocation(p.Id, "uNoise");
            p.TexMin     = _gl.GetUniformLocation(p.Id, "uTexMin");
            p.TexMax     = _gl.GetUniformLocation(p.Id, "uTexMax");
        }

        /// <summary>
        /// Compiles, links and caches the uniform locations of one program. A failure is reported
        /// and left as <c>Valid = false</c>: the render path falls back to the CRT program, and if
        /// that is the one that failed it simply draws nothing, as before.
        /// </summary>
        private ShaderProgram BuildProgram(string vertSrc, string fragSrc, string name)
        {
            var p = new ShaderProgram();

            uint vs = _gl.CreateShader(GLEnum.VertexShader);
            _gl.ShaderSource(vs, vertSrc);
            _gl.CompileShader(vs);
            bool vsOk = CheckShader(vs, $"Vertex Shader ({name})");

            uint fs = _gl.CreateShader(GLEnum.FragmentShader);
            _gl.ShaderSource(fs, fragSrc);
            _gl.CompileShader(fs);
            bool fsOk = CheckShader(fs, $"Fragment Shader ({name})");

            p.Id = _gl.CreateProgram();
            _gl.AttachShader(p.Id, vs);
            _gl.AttachShader(p.Id, fs);

            _gl.BindAttribLocation(p.Id, 0, "aPos");
            _gl.BindAttribLocation(p.Id, 1, "aTexCoord");

            _gl.LinkProgram(p.Id);
            _gl.GetProgram(p.Id, GLEnum.LinkStatus, out int linkStatus);
            if (linkStatus == 0)
            {
                ColoredConsole.WriteLine($"[GLControl] ERROR link ({name}): [[red]]{_gl.GetProgramInfoLog(p.Id)}[[/red]]", Config.ConfigOptions.DebugModes.Quiet);
                p.Valid = false;
            }
            else
            {
                p.Valid = vsOk && fsOk;
                ColoredConsole.WriteLine($"[GLControl] [[green]]GL linked ok ({name}).[[/green]]", Config.ConfigOptions.DebugModes.Quiet);
            }

            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);

            if (p.Valid)
            {
                CacheUniformLocations(p);

                // Constant uniforms, set once per program. The crop is identity by default and
                // updated each frame from ShowBorders.
                _gl.UseProgram(p.Id);
                if (p.Texture    >= 0) _gl.Uniform1(p.Texture, 0);
                if (p.SourceSize >= 0) _gl.Uniform2(p.SourceSize, (float)_srcW, (float)_srcH);
                if (p.TexMin     >= 0) _gl.Uniform2(p.TexMin, 0f, 0f);
                if (p.TexMax     >= 0) _gl.Uniform2(p.TexMax, 1f, 1f);
            }

            return p;
        }

        /// <summary>
        /// Program to draw this frame: the plain blit when the user turned the CRT effects off —
        /// or always in monochrome high resolution, where a sharp pixel image is what's wanted and
        /// CRT effects make no sense — the full shader otherwise (and also if the plain one failed
        /// to build). The user's DisableCrtEffects preference is left untouched, so it comes back
        /// when a colour monitor is selected again.
        /// </summary>
        private ShaderProgram ActiveProgram =>
            (VideoTiming.Mono || Config.ConfigOptions.RunninConfig.DisableCrtEffects) && _plainProgram != null && _plainProgram.Valid
                ? _plainProgram
                : _crtProgram;

        protected override unsafe void OnOpenGlInit(GlInterface gl)
        {
            _gl = GL.GetApi(gl.GetProcAddress);

            string glVersion = _gl.GetStringS(GLEnum.Version) ?? "Unknown";
            string glRenderer = _gl.GetStringS(GLEnum.Renderer) ?? "Unknown";
            string glslVer = _gl.GetStringS(GLEnum.ShadingLanguageVersion) ?? "Unknown";

            // Which shader set to use is decided by the context Avalonia actually created, not by
            // sniffing GL_VERSION for "OpenGL ES": that string is the driver's, and it says nothing
            // reliable about the profile (Mesa reports plain "3.1 Mesa …" for a desktop GLX context
            // on the same Raspberry Pi whose EGL path gives an ES one).
            bool isES = this.GlVersion.Type == GlProfileType.OpenGLES;
            int glslVersion = isES ? 100 : DesktopGlslVersion(glslVer);

            if (Config.ConfigOptions.RunninConfig.DebugMode >= Config.ConfigOptions.DebugModes.Quiet)
            {
                ColoredConsole.WriteLine($"[GLControl] GL Version  : [[green]]{glVersion}[[/green]]");
                ColoredConsole.WriteLine($"[GLControl] GL Renderer : [[green]]{glRenderer}[[/green]]");
                ColoredConsole.WriteLine($"[GLControl] GLSL Version: [[green]]{glslVer}[[/green]]");
                ColoredConsole.WriteLine($"[GLControl] Context     : [[green]]{(isES ? "OpenGL ES" : "Desktop OpenGL")} {this.GlVersion.Major}.{this.GlVersion.Minor}[[/green]], shaders: [[green]]#version {glslVersion}[[/green]]");
            }

            string vertSrc      = isES ? VertexShaderES        : $"#version {glslVersion}\n{VertexShaderDesktop}";
            string fragSrc      = isES ? FragmentShaderES      : $"#version {glslVersion}\n{FragmentShaderDesktop}";
            string fragPlainSrc = isES ? FragmentShaderPlainES : $"#version {glslVersion}\n{FragmentShaderPlainDesktop}";

            // Both programs are built up front — they are two small shaders, and building the
            // plain one lazily would stall the first frame after the user flips the switch.
            _crtProgram   = BuildProgram(vertSrc, fragSrc, "CRT");
            _plainProgram = BuildProgram(vertSrc, fragPlainSrc, "plain");

            float[] vertices =
            {
                -1.0f,  1.0f,  0.0f, 0.0f,
                -1.0f, -1.0f,  0.0f, 1.0f,
                 1.0f, -1.0f,  1.0f, 1.0f,
                 1.0f,  1.0f,  1.0f, 0.0f
            };

            _vbo = _gl.GenBuffer();
            _gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);

            fixed (void* v = vertices)
                _gl.BufferData(GLEnum.ArrayBuffer, (uint)(vertices.Length * sizeof(float)), v, GLEnum.StaticDraw);

            // VAO
            _hasVao = false;
            try
            {
                _vao = _gl.GenVertexArray();
                if (_vao != 0)
                {
                    _hasVao = true;
                    _gl.BindVertexArray(_vao);
                    _gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);

                    _gl.VertexAttribPointer(0, 2, GLEnum.Float, false, 4 * sizeof(float), (void*)0);
                    _gl.EnableVertexAttribArray(0);

                    _gl.VertexAttribPointer(1, 2, GLEnum.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
                    _gl.EnableVertexAttribArray(1);

                    _gl.BindVertexArray(0);
                    ColoredConsole.WriteLine($"[GLControl] VAO ok id=[[yellow]]{_vao}[[/yellow]]", Config.ConfigOptions.DebugModes.Quiet);
                }
                else
                {
                    ColoredConsole.WriteLine("[GLControl] GenVertexArray = [[yellow]]0[[/yellow]]", Config.ConfigOptions.DebugModes.Quiet);
                }
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"[GLControl] VAO not available -> [[red]]{ex.Message}[[/red]]", Config.ConfigOptions.DebugModes.Quiet);
                _hasVao = false;
                _vao = 0;
            }

            _gl.BindBuffer(GLEnum.ArrayBuffer, 0);

            _textureId = _gl.GenTexture();
            _gl.BindTexture(GLEnum.Texture2D, _textureId);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);

            // Allocated black at the maximum geometry rather than left undefined: the first
            // frames are drawn before the emulation has published anything, and nothing is
            // uploaded until it does. EnsureTextureGeometry resizes it to the active mode on the
            // first render (and again whenever the monitor type changes).
            uint[] blank = new uint[VideoTiming.MAX_WIDTH * VideoTiming.MAX_HEIGHT];
            fixed (void* pBlank = blank)
                _gl.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba, VideoTiming.MAX_WIDTH, VideoTiming.MAX_HEIGHT, 0,
                               GLEnum.Rgba, GLEnum.UnsignedByte, pBlank);

            _timer.Restart();
        }

        /// <summary>
        /// Resizes the texture to the active video geometry (colour 832x288 / mono 640x400) when
        /// VideoTiming's geometry generation changes — i.e. after a reset that switched the monitor
        /// type. Also refreshes uSourceSize on both programs. Cheap no-op on the common frame.
        /// </summary>
        private unsafe void EnsureTextureGeometry()
        {
            int gen = VideoTiming.GeometryGeneration;
            if (gen == _geomGen) return;

            _geomGen = gen;
            _srcW = VideoTiming.BUFFER_WIDTH;
            _srcH = VideoTiming.BUFFER_HEIGHT;

            _gl.BindTexture(GLEnum.Texture2D, _textureId);
            uint[] blank = new uint[_srcW * _srcH];
            fixed (void* pBlank = blank)
                _gl.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba, (uint)_srcW, (uint)_srcH, 0,
                               GLEnum.Rgba, GLEnum.UnsignedByte, pBlank);

            foreach (var p in new[] { _crtProgram, _plainProgram })
            {
                if (p != null && p.Valid && p.SourceSize >= 0)
                {
                    _gl.UseProgram(p.Id);
                    _gl.Uniform2(p.SourceSize, (float)_srcW, (float)_srcH);
                }
            }

            // Force the next AcquireFrame to hand over a buffer so the freshly-sized texture is
            // filled (its bookmark would otherwise still match and skip the upload).
            _frameSeq = -1;
        }

        protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
        {
            ShaderProgram prog = ActiveProgram;
            if (_gl == null || prog == null || prog.Id == 0 || !prog.Valid) return;

            long profileRenderStart = FrameProfiler.Stamp();

            _gl.BindFramebuffer(GLEnum.Framebuffer, (uint)fb);

            EnsureTextureGeometry();

            // DPI-aware surface size: the same pixel size Avalonia gives the framebuffer
            // (Bounds * RenderScaling, truncated).
            double scaling = ScreenScaling;
            uint surfaceW = (uint)Math.Max(1, (int)(Bounds.Width  * scaling));
            uint surfaceH = (uint)Math.Max(1, (int)(Bounds.Height * scaling));

            // Letterbox: the picture keeps DisplayAspectRatio whatever shape the control is,
            // centred, with the leftover cleared to black. In a normal window the window itself
            // is kept at the right ratio (MainWindow.EnforceAspectRatio) so this is usually a
            // no-op, but it is what makes the two cases where it cannot be work: full screen
            // (the window is the screen and its shape is not ours to choose) and the transient
            // shapes of an interactive resize — which used to stretch the picture until the
            // 300 ms debounce corrected the window.
            double ratio = DisplayAspectRatio;
            uint outW = surfaceW, outH = surfaceH;

            if (surfaceW > surfaceH * ratio)
                outW = (uint)Math.Max(1, Math.Round(surfaceH * ratio));   // too wide -> pillarbox
            else
                outH = (uint)Math.Max(1, Math.Round(surfaceW / ratio));   // too tall -> letterbox

            int outX = (int)(surfaceW - outW) / 2;
            int outY = (int)(surfaceH - outH) / 2;

            _gl.Viewport(outX, outY, outW, outH);

            // Log on first frame and whenever the DPI scale changes (window moved to a
            // monitor with a different scale) — this is what users should report when the
            // picture doesn't fill the window.
            if (_firstRender || scaling != _lastLoggedScaling)
            {
                ColoredConsole.WriteLine($"[GLControl] fb=[[yellow]]{fb}[[/yellow]], hasVao=[[yellow]]{_hasVao}[[/yellow]], scale=[[yellow]]{scaling:0.##}[[/yellow]], viewport=[[yellow]]{outW}x{outH}[[/yellow]]+[[yellow]]{outX},{outY}[[/yellow]], surface=[[yellow]]{surfaceW}x{surfaceH}[[/yellow]], bounds=[[yellow]]{Bounds.Width:0.#}x{Bounds.Height:0.#}[[/yellow]]", Config.ConfigOptions.DebugModes.Quiet);
                _firstRender = false;
                _lastLoggedScaling = scaling;
            }

            // Reset
            _gl.Disable(GLEnum.CullFace);
            _gl.Disable(GLEnum.DepthTest);
            _gl.Disable(GLEnum.StencilTest);
            _gl.Disable(GLEnum.ScissorTest);
            _gl.Disable(GLEnum.Blend);
            _gl.ColorMask(true, true, true, true);
            _gl.DepthMask(false);

            // Black, and it matters: with the scissor test off (disabled just above) this clear
            // covers the whole surface, so it is what paints the letterbox bars around the
            // viewport set above. A grey there would frame the picture on every screen wider
            // than 4:3.
            _gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
            _gl.Clear((uint)GLEnum.ColorBufferBit);

            _gl.UseProgram(prog.Id);

            // Vertex state
            if (_hasVao)
            {
                _gl.BindVertexArray(_vao);
            }
            else
            {
                _gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);
                _gl.VertexAttribPointer(0, 2, GLEnum.Float, false, 4 * sizeof(float), (void*)0);
                _gl.EnableVertexAttribArray(0);
                _gl.VertexAttribPointer(1, 2, GLEnum.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
                _gl.EnableVertexAttribArray(1);
            }

            _gl.ActiveTexture(GLEnum.Texture0);
            _gl.BindTexture(GLEnum.Texture2D, _textureId);

            // Uniforms per frame. The effect ones are simply absent (-1) from the plain program,
            // so this same block sends nothing extra when the effects are switched off.
            float time = (float)_timer.Elapsed.TotalSeconds;

            if (prog.OutputSize >= 0) _gl.Uniform2(prog.OutputSize, (float)outW, (float)outH);
            if (prog.Time       >= 0) _gl.Uniform1(prog.Time, time);

            if (prog.Curvature >= 0) _gl.Uniform1(prog.Curvature, Config.ConfigOptions.RunninConfig.Curvature);
            if (prog.Vignette  >= 0) _gl.Uniform1(prog.Vignette,  Config.ConfigOptions.RunninConfig.Vignette);
            if (prog.Scanline  >= 0) _gl.Uniform1(prog.Scanline,  Config.ConfigOptions.RunninConfig.Scanline);
            if (prog.ChromAb   >= 0) _gl.Uniform1(prog.ChromAb,   Config.ConfigOptions.RunninConfig.ChromAb);
            if (prog.Bloom     >= 0) _gl.Uniform1(prog.Bloom,     Config.ConfigOptions.RunninConfig.Bloom);
            if (prog.Mask      >= 0) _gl.Uniform1(prog.Mask,      Config.ConfigOptions.RunninConfig.Mask);
            if (prog.Noise     >= 0) _gl.Uniform1(prog.Noise,     Config.ConfigOptions.RunninConfig.Noise);

            // Border crop: identity (full texture) when borders are shown or in monochrome (which
            // has none), otherwise the 320x200 display sub-rectangle so the picture fills the
            // window as before.
            if (prog.TexMin >= 0 || prog.TexMax >= 0)
            {
                bool crop = !VideoTiming.Mono && !Config.ConfigOptions.RunninConfig.ShowBorders;
                float xMin = crop ? (float)VideoTiming.DISPLAY_ORIGIN_X / _srcW : 0f;
                float yMin = crop ? (float)VideoTiming.DISPLAY_ORIGIN_Y / _srcH : 0f;
                float xMax = crop ? (float)(VideoTiming.DISPLAY_ORIGIN_X + VideoTiming.DISPLAY_TEX_WIDTH) / _srcW : 1f;
                float yMax = crop ? (float)(VideoTiming.DISPLAY_ORIGIN_Y + VideoTiming.DISPLAY_TEX_HEIGHT) / _srcH : 1f;
                if (prog.TexMin >= 0) _gl.Uniform2(prog.TexMin, xMin, yMin);
                if (prog.TexMax >= 0) _gl.Uniform2(prog.TexMax, xMax, yMax);
            }

            // Upload the emulation's latest frame, if there is a new one. AcquireFrame hands over
            // the buffer itself (no copy) and returns null when the texture is already up to date,
            // in which case the picture is simply redrawn from what the texture already holds.
            long profileUpload = FrameProfiler.Stamp();
            uint[] frame = ASEMain.AcquireFrame(ref _frameSeq);
            if (frame != null)
            {
                fixed (void* pData = frame)
                {
                    _gl.TexSubImage2D(GLEnum.Texture2D, 0, 0, 0, (uint)_srcW, (uint)_srcH,
                                      GLEnum.Rgba, GLEnum.UnsignedByte, pData);
                }
            }
            long profileUploadEnd = FrameProfiler.Stamp();

            _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

            // Report to --profile. Only the CPU side of drawing is timed here: the draw call
            // returns without waiting for the GPU, so a shader too heavy for the hardware shows
            // up as a lower GL frame rate rather than as a longer render time.
            if (FrameProfiler.Enabled)
                FrameProfiler.ReportGlFrame(Stopwatch.GetTimestamp() - profileRenderStart,
                                            profileUploadEnd - profileUpload,
                                            frame != null);

            RequestNextFrameRendering();
        }



        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            _gl?.DeleteBuffer(_vbo);
            if (_hasVao && _vao != 0)
                _gl?.DeleteVertexArray(_vao);
            _gl?.DeleteTexture(_textureId);
            if (_crtProgram != null && _crtProgram.Id != 0) _gl?.DeleteProgram(_crtProgram.Id);
            if (_plainProgram != null && _plainProgram.Id != 0) _gl?.DeleteProgram(_plainProgram.Id);
            _crtProgram = null;
            _plainProgram = null;
            base.OnOpenGlDeinit(gl);
        }
    }
}
