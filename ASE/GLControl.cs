using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Silk.NET.OpenGL;
using System;
using System.Diagnostics;

namespace ASE
{
    public class GLControl : OpenGlControlBase
    {
        // Shaders — Desktop OpenGL 3.2+ Core Profile
        private const string VertexShaderDesktop = @"#version 150

            in vec2 aPos;
            in vec2 aTexCoord;
            out vec2 v_texCoord;

            void main() {
                gl_Position = vec4(aPos, 0.0, 1.0);
                v_texCoord  = aTexCoord;
            }";

        private const string FragmentShaderDesktop = @"#version 150

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

        // Shaders — OpenGL ES 2.0 / 3.0  (ANGLE / Metal)
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

        // =====================================================================

        private GL _gl;
        private uint _textureId;
        private uint _program;
        private bool _programValid;
        private uint _vao;
        private uint _vbo;
        private bool _hasVao;
        private bool _firstRender = true;
        private double _lastLoggedScaling = -1;

        // uniforms: cache de locations
        private int _uTextureLoc;
        private int _uSourceSizeLoc;
        private int _uOutputSizeLoc;
        private int _uTimeLoc;
        private int _uCurvatureLoc;
        private int _uVignetteLoc;
        private int _uScanlineLoc;
        private int _uChromAbLoc;
        private int _uBloomLoc;
        private int _uMaskLoc;
        private int _uNoiseLoc;
        private int _uTexMinLoc;
        private int _uTexMaxLoc;

        private readonly Stopwatch _timer = new Stopwatch();

        // Atari screen size
        private const int SrcW = ASEMain.ScreenWidth;
        private const int SrcH = ASEMain.ScreenHeight / 2;

        private readonly byte[] pixels = new byte[SrcW * SrcH * 4];

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

        private void CacheUniformLocations()
        {
            _uTextureLoc    = _gl.GetUniformLocation(_program, "uTexture");
            _uSourceSizeLoc = _gl.GetUniformLocation(_program, "uSourceSize");
            _uOutputSizeLoc = _gl.GetUniformLocation(_program, "uOutputSize");
            _uTimeLoc       = _gl.GetUniformLocation(_program, "uTime");
            _uCurvatureLoc  = _gl.GetUniformLocation(_program, "uCurvature");
            _uVignetteLoc   = _gl.GetUniformLocation(_program, "uVignette");
            _uScanlineLoc   = _gl.GetUniformLocation(_program, "uScanline");
            _uChromAbLoc    = _gl.GetUniformLocation(_program, "uChromAb");
            _uBloomLoc      = _gl.GetUniformLocation(_program, "uBloom");
            _uMaskLoc       = _gl.GetUniformLocation(_program, "uMask");
            _uNoiseLoc      = _gl.GetUniformLocation(_program, "uNoise");
            _uTexMinLoc     = _gl.GetUniformLocation(_program, "uTexMin");
            _uTexMaxLoc     = _gl.GetUniformLocation(_program, "uTexMax");
        }

        protected override unsafe void OnOpenGlInit(GlInterface gl)
        {
            _gl = GL.GetApi(gl.GetProcAddress);

            string glVersion = _gl.GetStringS(GLEnum.Version) ?? "Unknown";
            string glRenderer = _gl.GetStringS(GLEnum.Renderer) ?? "Unknown";
            string glslVer = _gl.GetStringS(GLEnum.ShadingLanguageVersion) ?? "Unknown";
            bool isES = glVersion.Contains("OpenGL ES");
            
            if (Config.ConfigOptions.RunninConfig.DebugMode >= Config.ConfigOptions.DebugModes.Quiet)
            {
                ColoredConsole.WriteLine($"[GLControl] GL Version  : [[green]]{glVersion}[[/green]]");
                ColoredConsole.WriteLine($"[GLControl] GL Renderer : [[green]]{glRenderer}[[/green]]");
                ColoredConsole.WriteLine($"[GLControl] GLSL Version: [[green]]{glslVer}[[/green]]");
                ColoredConsole.WriteLine($"[GLControl] Context     : [[green]]{(isES ? "OpenGL ES" : "Desktop OpenGL")}[[/green]]");
            }

            string vertSrc = isES ? VertexShaderES   : VertexShaderDesktop;
            string fragSrc = isES ? FragmentShaderES : FragmentShaderDesktop;

            uint vs = _gl.CreateShader(GLEnum.VertexShader);
            _gl.ShaderSource(vs, vertSrc);
            _gl.CompileShader(vs);
            bool vsOk = CheckShader(vs, "Vertex Shader");

            uint fs = _gl.CreateShader(GLEnum.FragmentShader);
            _gl.ShaderSource(fs, fragSrc);
            _gl.CompileShader(fs);
            bool fsOk = CheckShader(fs, "Fragment Shader");

            _program = _gl.CreateProgram();
            _gl.AttachShader(_program, vs);
            _gl.AttachShader(_program, fs);

            _gl.BindAttribLocation(_program, 0, "aPos");
            _gl.BindAttribLocation(_program, 1, "aTexCoord");

            _gl.LinkProgram(_program);
            _gl.GetProgram(_program, GLEnum.LinkStatus, out int linkStatus);
            if (linkStatus == 0)
            {
                ColoredConsole.WriteLine($"[GLControl] ERROR link: [[red]]{_gl.GetProgramInfoLog(_program)}[[/red]]", Config.ConfigOptions.DebugModes.Quiet);
                _programValid = false;
            }
            else
            {
                _programValid = vsOk && fsOk;
                ColoredConsole.WriteLine($"[GLControl] [[green]]GL linked ok.[[/green]]", Config.ConfigOptions.DebugModes.Quiet);
            }

            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);

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

            _gl.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba, SrcW, SrcH, 0,
                           GLEnum.Rgba, GLEnum.UnsignedByte, null);

            _gl.UseProgram(_program);
            CacheUniformLocations();

            if (_uTextureLoc >= 0)    _gl.Uniform1(_uTextureLoc, 0);
            if (_uSourceSizeLoc >= 0) _gl.Uniform2(_uSourceSizeLoc, (float)SrcW, (float)SrcH);
            // Identity crop by default; updated each frame from ShowBorders.
            if (_uTexMinLoc >= 0) _gl.Uniform2(_uTexMinLoc, 0f, 0f);
            if (_uTexMaxLoc >= 0) _gl.Uniform2(_uTexMaxLoc, 1f, 1f);

            _timer.Restart();
        }

        protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (_gl == null || _program == 0 || !_programValid) return;

            _gl.BindFramebuffer(GLEnum.Framebuffer, (uint)fb);

            // DPI-aware viewport
            double scaling = (VisualRoot as Avalonia.Controls.TopLevel)?.RenderScaling ?? 1.0;
            uint outW = (uint)Math.Max(1, (int)(Bounds.Width  * scaling));
            uint outH = (uint)Math.Max(1, (int)(Bounds.Height * scaling));
            _gl.Viewport(0, 0, outW, outH);

            // Log on first frame and whenever the DPI scale changes (window moved to a
            // monitor with a different scale) — this is what users should report when the
            // picture doesn't fill the window.
            if (_firstRender || scaling != _lastLoggedScaling)
            {
                ColoredConsole.WriteLine($"[GLControl] fb=[[yellow]]{fb}[[/yellow]], hasVao=[[yellow]]{_hasVao}[[/yellow]], scale=[[yellow]]{scaling:0.##}[[/yellow]], viewport=[[yellow]]{outW}x{outH}[[/yellow]], bounds=[[yellow]]{Bounds.Width:0.#}x{Bounds.Height:0.#}[[/yellow]]", Config.ConfigOptions.DebugModes.Quiet);
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

            _gl.ClearColor(0.1f, 0.1f, 0.1f, 1.0f);
            _gl.Clear((uint)GLEnum.ColorBufferBit);

            _gl.UseProgram(_program);

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

            // Uniforms per frame
            float time = (float)_timer.Elapsed.TotalSeconds;

            if (_uOutputSizeLoc >= 0) _gl.Uniform2(_uOutputSizeLoc, (float)outW, (float)outH);
            if (_uTimeLoc       >= 0) _gl.Uniform1(_uTimeLoc, time);

            if (_uCurvatureLoc >= 0) _gl.Uniform1(_uCurvatureLoc, Config.ConfigOptions.RunninConfig.Curvature);
            if (_uVignetteLoc  >= 0) _gl.Uniform1(_uVignetteLoc,  Config.ConfigOptions.RunninConfig.Vignette);
            if (_uScanlineLoc  >= 0) _gl.Uniform1(_uScanlineLoc,  Config.ConfigOptions.RunninConfig.Scanline);
            if (_uChromAbLoc   >= 0) _gl.Uniform1(_uChromAbLoc,   Config.ConfigOptions.RunninConfig.ChromAb);
            if (_uBloomLoc     >= 0) _gl.Uniform1(_uBloomLoc,     Config.ConfigOptions.RunninConfig.Bloom);
            if (_uMaskLoc      >= 0) _gl.Uniform1(_uMaskLoc,      Config.ConfigOptions.RunninConfig.Mask);
            if (_uNoiseLoc     >= 0) _gl.Uniform1(_uNoiseLoc,     Config.ConfigOptions.RunninConfig.Noise);

            // Border crop: identity (full texture) when borders are shown, otherwise the 320x200
            // display sub-rectangle so the picture fills the window as before.
            if (_uTexMinLoc >= 0 || _uTexMaxLoc >= 0)
            {
                bool showBorders = Config.ConfigOptions.RunninConfig.ShowBorders;
                float xMin = showBorders ? 0f : (float)VideoTiming.DISPLAY_ORIGIN_X / SrcW;
                float yMin = showBorders ? 0f : (float)VideoTiming.DISPLAY_ORIGIN_Y / SrcH;
                float xMax = showBorders ? 1f : (float)(VideoTiming.DISPLAY_ORIGIN_X + VideoTiming.DISPLAY_TEX_WIDTH) / SrcW;
                float yMax = showBorders ? 1f : (float)(VideoTiming.DISPLAY_ORIGIN_Y + VideoTiming.DISPLAY_TEX_HEIGHT) / SrcH;
                if (_uTexMinLoc >= 0) _gl.Uniform2(_uTexMinLoc, xMin, yMin);
                if (_uTexMaxLoc >= 0) _gl.Uniform2(_uTexMaxLoc, xMax, yMax);
            }

            // upload to framebuffer
            byte[] pixelData = ObtenerPixelesRGBA();
            fixed (void* pData = pixelData)
            {
                _gl.TexSubImage2D(GLEnum.Texture2D, 0, 0, 0, SrcW, SrcH,
                                  GLEnum.Rgba, GLEnum.UnsignedByte, pData);
            }

            _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

            RequestNextFrameRendering();
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            _gl?.DeleteBuffer(_vbo);
            if (_hasVao && _vao != 0)
                _gl?.DeleteVertexArray(_vao);
            _gl?.DeleteTexture(_textureId);
            _gl?.DeleteProgram(_program);
            base.OnOpenGlDeinit(gl);
        }

        private byte[] ObtenerPixelesRGBA()
        {
            ASEMain.SnapshotScreen(pixels);
            return pixels;
        }
    }
}
